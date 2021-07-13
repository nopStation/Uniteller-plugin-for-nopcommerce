using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Nop.Core;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Services.Plugins;
using Nop.Services.Configuration;
using Nop.Services.Directory;
using Nop.Services.Localization;
using Nop.Services.Payments;
using Nop.Web.Framework;
using System.Threading.Tasks;

namespace Nop.Plugin.Payments.Uniteller
{
    /// <summary>
    /// Uniteller payment method
    /// </summary>
    public class UnitellerPaymentProcessor : BasePlugin, IPaymentMethod
    {
        #region Fields

        private readonly ICurrencyService _currencyService;
        private readonly ILocalizationService _localizationService;
        private readonly IPaymentService _paymentService;
        private readonly ISettingService _settingService;
        private readonly IWebHelper _webHelper;
        private readonly CurrencySettings _currencySettings;
        private readonly UnitellerPaymentSettings _unitellerPaymentSettings;

        private const string UNITELLER_URL = "https://wpay.uniteller.ru/pay/";
        private const string UNITELLER_RESULTS_URL = "https://wpay.uniteller.ru/results/";

        private const string RETURN_FORMAT = "4"; // XML return format

        #endregion

        #region Ctor

        public UnitellerPaymentProcessor(ICurrencyService currencyService,
            ILocalizationService localizationService,
            IPaymentService paymentService,
            ISettingService settingService,
            IWebHelper webHelper,
            CurrencySettings currencySettings,
            UnitellerPaymentSettings unitellerPaymentSettings)
        {
            this._currencyService = currencyService;
            this._localizationService = localizationService;
            this._paymentService = paymentService;
            this._settingService = settingService;
            this._webHelper = webHelper;
            this._currencySettings = currencySettings;
            this._unitellerPaymentSettings = unitellerPaymentSettings;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Process a payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public Task<ProcessPaymentResult> ProcessPaymentAsync(ProcessPaymentRequest processPaymentRequest)
        {
            return Task.FromResult(new ProcessPaymentResult { NewPaymentStatus = PaymentStatus.Pending });
        }

        /// <summary>
        /// Post process payment (used by payment gateways that require redirecting to a third-party URL)
        /// </summary>
        /// <param name="postProcessPaymentRequest">Payment info required for an order processing</param>
        public  async Task PostProcessPaymentAsync(PostProcessPaymentRequest postProcessPaymentRequest)
        {
            var customerId = postProcessPaymentRequest.Order.CustomerId;
            var orderGuid = postProcessPaymentRequest.Order.OrderGuid;
            var orderTotal = postProcessPaymentRequest.Order.OrderTotal;
            var amount = string.Format(CultureInfo.InvariantCulture, "{0:0.00}", orderTotal);
            var orderId = orderGuid.ToString();
            var customerIdp = customerId.ToString();

            //create and send post data
            var post = new RemotePost
            {
                FormName = "PayPoint",
                Url = UNITELLER_URL
            };
            post.Add("Shop_IDP", _unitellerPaymentSettings.ShopIdp);
            post.Add("Order_IDP", orderId);
            post.Add("Currency", (await _currencyService.GetCurrencyByIdAsync(_currencySettings.PrimaryStoreCurrencyId)).CurrencyCode);
            post.Add("Subtotal_P", amount);
            post.Add("Customer_IDP", customerIdp);

            var hShopIdp = GetMD5(_unitellerPaymentSettings.ShopIdp);
            var hOrderIdp = GetMD5(orderId);
            var hAmount = GetMD5(amount);
            var hCustomerIdp = GetMD5(customerIdp);
            var hPassword = GetMD5(_unitellerPaymentSettings.Password);
            var empty = GetMD5(string.Empty);

            const string fSignature = "{0}&{1}&{2}&{5}&{5}&{5}&{3}&{5}&{5}&{5}&{4}";

            //code to identify the sender and check integrity of files
            var signature = GetMD5(string.Format(fSignature, hShopIdp, hOrderIdp, hAmount, hCustomerIdp, hPassword, empty)).ToUpper();

            post.Add("Signature", signature);
            //uniteller considers localhost wrong address
            var siteUrl = _webHelper.GetStoreLocation().Replace("localhost", "127.0.0.1");
            var failUrl = $"{siteUrl}Plugins/Uniteller/CancelOrder";
            var successUrl = $"{siteUrl}Plugins/Uniteller/Success";

            post.Add("URL_RETURN_NO", failUrl);
            post.Add("URL_RETURN_OK", successUrl);

            post.Post();
        }

        /// <summary>
        /// Get the status of the order in the system Uniteller
        /// </summary>
        /// <param name="orderId">Order ID</param>
        /// <returns></returns>
        public string[] GetPaymentStatus(string orderId)
        {
            //create and send post data
            var postData = new NameValueCollection
            {
                {"Shop_ID", _unitellerPaymentSettings.ShopIdp},
                {"Login", _unitellerPaymentSettings.Login},
                {"Password", _unitellerPaymentSettings.Password},
                {"Format", RETURN_FORMAT},
                {"ShopOrderNumber", orderId},
                {"S_FIELDS", "Status"}
            };


            byte[] data;
            using (var client = new WebClient())
            {
                data = client.UploadValues(UNITELLER_RESULTS_URL, postData);
            }

            using (var ms = new MemoryStream(data))
            {
                using (var sr = new StreamReader(ms))
                {
                    var rez = sr.ReadToEnd();

                    if (!rez.Contains("?xml"))
                        return new[] {string.Empty};


                    var doc = XDocument.Parse(rez);

                    return doc.Root?.Element("orders")?.Element("order")?.Elements("status")
                               .Select(p => p.Value.ToUpper())
                               .ToArray() ?? new[] {string.Empty};
                }
            }
        }

        /// <summary>
        /// Creates an MD5 hash sum from string
        /// </summary>
        /// <param name="strToMD5">String to create an MD5 hash sum</param>
        /// <returns>MD5 hash sum</returns>
        public static string GetMD5(string strToMD5)
        {
            var enc = Encoding.Default.GetEncoder();
            var length = strToMD5.Length;
            var data = new byte[length];
            enc.GetBytes(strToMD5.ToCharArray(), 0, length, data, 0, true);
            byte[] result;

            using (var md5 = new MD5CryptoServiceProvider())
            {
                result = md5.ComputeHash(data);
            }

            return BitConverter.ToString(result)
                .Replace("-", string.Empty).ToLower();
        }

        /// <summary>
        /// Returns a value indicating whether payment method should be hidden during checkout
        /// </summary>
        /// <param name="cart">Shopping cart</param>
        /// <returns>true - hide; false - display.</returns>
        public  Task<bool> HidePaymentMethodAsync(IList<ShoppingCartItem> cart)
        {
            //you can put any logic here
            //for example, hide this payment method if all products in the cart are downloadable
            //or hide this payment method if current customer is from certain country
            return Task.FromResult(false);
        }

        /// <summary>
        /// Gets additional handling fee
        /// </summary>
        /// <param name="cart">Shopping cart</param>
        /// <returns>Additional handling fee</returns>
        public  async Task<decimal> GetAdditionalHandlingFeeAsync(IList<ShoppingCartItem> cart)
        {
            var result = await _paymentService.CalculateAdditionalFeeAsync(cart,
                _unitellerPaymentSettings.AdditionalFee, _unitellerPaymentSettings.AdditionalFeePercentage);

            return result;
        }

        /// <summary>
        /// Gets a value indicating whether customers can complete a payment after order is placed but not completed (for redirection payment methods)
        /// </summary>
        /// <param name="order">Order</param>
        /// <returns>Result</returns>
        public  Task<bool> CanRePostProcessPaymentAsync(Order order)
        {
            //let's ensure that at least 5 seconds passed after order is placed
            //P.S. there's no any particular reason for that. we just do it
            return Task.FromResult(!((DateTime.UtcNow - order.CreatedOnUtc).TotalSeconds < 5));
        }

        public override string GetConfigurationPageUrl()
        {
            return $"{_webHelper.GetStoreLocation()}Admin/PaymentUniteller/Configure";
        }

        public Task<IList<string>> ValidatePaymentFormAsync(IFormCollection form)
        {
            return Task.FromResult<IList<string>>(new List<string>());
        }

        public  Task<ProcessPaymentRequest> GetPaymentInfoAsync(IFormCollection form)
        {
            return Task.FromResult(new ProcessPaymentRequest());
        }

        public string GetPublicViewComponentName()
        {
            return "PaymentUniteller";
        }
 
        /// <summary>
        /// Install plugin method
        /// </summary>
        public override  async Task InstallAsync()
        {
            //settings
            var settings = new UnitellerPaymentSettings();
            await _settingService.SaveSettingAsync(settings);

            //locales
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.ShopIdp", "The Uniteller Point ID");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.ShopIdp.Hint", "Specify the Uniteller Point ID of your store on the website uniteller.ru.");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.Password", "Password");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.Password.Hint", "Set the password.");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.Login", "Login");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.Login.Hint", "Set the login.");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.AdditionalFee", "Additional fee");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.AdditionalFee.Hint", "Enter additional fee to charge your customers.");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.AdditionalFeePercentage", "Additional fee. Use percentage");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.AdditionalFeePercentage.Hint", "Determines whether to apply a percentage additional fee to the order total. If not enabled, a fixed value is used.");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.RedirectionTip", "For payment you will be redirected to the website uniteller.ru.");
            await _localizationService.AddOrUpdateLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.PaymentMethodDescription", "For payment you will be redirected to the website uniteller.ru.");

            await base.InstallAsync();
        }

        /// <summary>
        /// Uninstall plugin method
        /// </summary>
        public override async Task UninstallAsync()
        {
            //settings
            await _settingService.DeleteSettingAsync<UnitellerPaymentSettings>();

            //locales
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.ShopIdp");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.ShopIdp.Hint");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.Password");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.Password.Hint");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.Login");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.Login.Hint");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.AdditionalFee");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.AdditionalFee.Hint");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.AdditionalFeePercentage");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.AdditionalFeePercentage.Hint");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.RedirectionTip");
            await _localizationService.DeleteLocaleResourceAsync("Plugins.Payments.Uniteller.Fields.PaymentMethodDescription");

            await base.UninstallAsync();
        }

        /// <summary>
        /// Captures payment
        /// </summary>
        /// <param name="capturePaymentRequest">Capture payment request</param>
        /// <returns>Capture payment result</returns>
        public  Task<CapturePaymentResult> CaptureAsync(CapturePaymentRequest capturePaymentRequest)
        {
            var result = new CapturePaymentResult();
            result.AddError("Capture method not supported");
            return Task.FromResult(result);
        }

        /// <summary>
        /// Refunds a payment
        /// </summary>
        /// <param name="refundPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public  Task<RefundPaymentResult> RefundAsync(RefundPaymentRequest refundPaymentRequest)
        {
            var result = new RefundPaymentResult();
            result.AddError("Refund method not supported");
            return Task.FromResult(result);
        }

        /// <summary>
        /// Voids a payment
        /// </summary>
        /// <param name="voidPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public  Task<VoidPaymentResult> VoidAsync(VoidPaymentRequest voidPaymentRequest)
        {
            var result = new VoidPaymentResult();
            result.AddError("Void method not supported");
            return Task.FromResult(result);
        }

        /// <summary>
        /// Process recurring payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public  Task<ProcessPaymentResult> ProcessRecurringPaymentAsync(ProcessPaymentRequest processPaymentRequest)
        {
            var result = new ProcessPaymentResult();
            result.AddError("Recurring payment not supported");
            return Task.FromResult(result);
        }

        /// <summary>
        /// Cancels a recurring payment
        /// </summary>
        /// <param name="cancelPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public Task<CancelRecurringPaymentResult> CancelRecurringPaymentAsync(CancelRecurringPaymentRequest cancelPaymentRequest)
        {
            var result = new CancelRecurringPaymentResult();
            result.AddError("Recurring payment not supported");
            return Task.FromResult(result);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether capture is supported
        /// </summary>
        public bool SupportCapture
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a value indicating whether partial refund is supported
        /// </summary>
        public bool SupportPartiallyRefund
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a value indicating whether refund is supported
        /// </summary>
        public bool SupportRefund
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a value indicating whether void is supported
        /// </summary>
        public bool SupportVoid
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a recurring payment type of payment method
        /// </summary>
        public RecurringPaymentType RecurringPaymentType
        {
            get { return RecurringPaymentType.NotSupported; }
        }

        /// <summary>
        /// Gets a payment method type
        /// </summary>
        public PaymentMethodType PaymentMethodType
        {
            get { return PaymentMethodType.Redirection; }
        }

        /// <summary>
        /// Gets a value indicating whether we should display a payment information page for this plugin
        /// </summary>
        public bool SkipPaymentInfo
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a payment method description that will be displayed on checkout pages in the public store
        /// </summary>
        public async Task<string> GetPaymentMethodDescriptionAsync()
        {
            return await _localizationService.GetResourceAsync("Plugins.Payments.Uniteller.Fields.PaymentMethodDescription");
        }

        #endregion
    }
}
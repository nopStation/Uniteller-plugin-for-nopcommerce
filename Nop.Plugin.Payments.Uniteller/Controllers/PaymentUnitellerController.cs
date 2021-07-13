using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nop.Core;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.Uniteller.Models;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Messages;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Security;
using Nop.Web.Framework;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Mvc.Filters;

namespace Nop.Plugin.Payments.Uniteller.Controllers
{
    public class PaymentUnitellerController : BasePaymentController
    {
        private readonly ILocalizationService _localizationService;
        private readonly ILogger _logger;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly IOrderService _orderService;
        private readonly IPaymentService _paymentService;
        private readonly IPermissionService _permissionService;
        private readonly ISettingService _settingService;
        private readonly IStoreContext _storeContext;
        private readonly IWebHelper _webHelper;
        private readonly INotificationService _notificationService;
        private readonly IPaymentPluginManager _paymentPluginManager;

        public PaymentUnitellerController(ILocalizationService localizationService,
            ILogger logger,
            IOrderProcessingService orderProcessingService,
            IOrderService orderService,
            IPaymentService paymentService,
            IPermissionService permissionService,
            ISettingService settingService,
            IStoreContext storeContext,
            IWebHelper webHelper,
            INotificationService notificationService,
            IPaymentPluginManager paymentPluginManager)
        {
            _localizationService = localizationService;
            _logger = logger;
            _orderProcessingService = orderProcessingService;
            _orderService = orderService;
            _paymentService = paymentService;
            _permissionService = permissionService;
            _settingService = settingService;
            _storeContext = storeContext;
            _webHelper = webHelper;
            _notificationService = notificationService;
            _paymentPluginManager = paymentPluginManager;
        }

        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> Configure()
        {
            if (!await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var unitellerPaymentSettings = await _settingService.LoadSettingAsync<UnitellerPaymentSettings>(storeScope);

            var model = new ConfigurationModel
            {
                ShopIdp = unitellerPaymentSettings.ShopIdp,
                Login = unitellerPaymentSettings.Login,
                Password = unitellerPaymentSettings.Password,
                AdditionalFee = unitellerPaymentSettings.AdditionalFee,
                AdditionalFeePercentage = unitellerPaymentSettings.AdditionalFeePercentage,
                ActiveStoreScopeConfiguration = storeScope
            };

            if (storeScope > 0)
            {
                model.ShopIdpOverrideForStore = await _settingService.SettingExistsAsync(unitellerPaymentSettings, x => x.ShopIdp, storeScope);
                model.LoginOverrideForStore = await _settingService.SettingExistsAsync(unitellerPaymentSettings, x => x.Login, storeScope);
                model.PasswordOverrideForStore = await _settingService.SettingExistsAsync(unitellerPaymentSettings, x => x.Password, storeScope);
                model.AdditionalFeeOverrideForStore = await _settingService.SettingExistsAsync(unitellerPaymentSettings, x => x.AdditionalFee, storeScope);
                model.AdditionalFeePercentageOverrideForStore = await _settingService.SettingExistsAsync(unitellerPaymentSettings, x => x.AdditionalFeePercentage, storeScope);
            }

            return View("~/Plugins/Payments.Uniteller/Views/Configure.cshtml", model);
        }
        
        [HttpPost]
        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public async Task<IActionResult> Configure(ConfigurationModel model)
        {
            if (!await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var unitellerPaymentSettings = await _settingService.LoadSettingAsync<UnitellerPaymentSettings>(storeScope);

            //save settings
            unitellerPaymentSettings.ShopIdp = model.ShopIdp;
            unitellerPaymentSettings.Login = model.Login;
            unitellerPaymentSettings.Password = model.Password;
            unitellerPaymentSettings.AdditionalFee = model.AdditionalFee;
            unitellerPaymentSettings.AdditionalFeePercentage = model.AdditionalFeePercentage;

            /* We do not clear cache after each setting update.
             * This behavior can increase performance because cached settings will not be cleared 
             * and loaded from database after each update */
             await _settingService.SaveSettingOverridablePerStoreAsync(unitellerPaymentSettings, x => x.ShopIdp, model.ShopIdpOverrideForStore, storeScope, false);
             await _settingService.SaveSettingOverridablePerStoreAsync(unitellerPaymentSettings, x => x.Login, model.LoginOverrideForStore, storeScope, false);
             await _settingService.SaveSettingOverridablePerStoreAsync(unitellerPaymentSettings, x => x.Password, model.PasswordOverrideForStore, storeScope, false);
             await _settingService.SaveSettingOverridablePerStoreAsync(unitellerPaymentSettings, x => x.AdditionalFee, model.AdditionalFeeOverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(unitellerPaymentSettings, x => x.AdditionalFeePercentage, model.AdditionalFeePercentageOverrideForStore, storeScope, false);

            //now clear settings cache
            await _settingService.ClearCacheAsync();

           _notificationService. SuccessNotification(await _localizationService.GetResourceAsync("Admin.Plugins.Saved"));

            return RedirectToAction("Configure");
        }
        
        private ContentResult GetResponse(string textToResponse, bool success = false)
        {
            var msg = success ? "SUCCESS" : "FAIL";
            if (!success)
                _logger.ErrorAsync($"Uniteller. {textToResponse}");
           
            return Content($"{msg}\r\nnopCommerce. {textToResponse}", "text/plain", Encoding.UTF8);
        }

        private string GetValue(string key, IFormCollection form)
        {
            return (form.Keys.Contains(key) ? form[key].ToString() : _webHelper.QueryString<string>(key)) ?? string.Empty;
        }

        private async Task<IActionResult> UpdateOrderStatus(Order order, string status)
        {
            status = status.ToUpper();
            var textToResponse = "Your order has been paid";

            switch (status)
            {
                case "CANCELED":
                {
                    //mark order as canceled
                    if ((order.PaymentStatus == PaymentStatus.Paid || order.PaymentStatus == PaymentStatus.Authorized) &&
                        _orderProcessingService.CanCancelOrder(order))
                            await _orderProcessingService.CancelOrderAsync(order, true);

                    textToResponse = "Your order has been canceled";
                }
                    break;
                case "AUTHORIZED":
                {
                    //mark order as authorized
                    if (_orderProcessingService.CanMarkOrderAsAuthorized(order))
                            await _orderProcessingService.MarkAsAuthorizedAsync(order);
                    textToResponse = "Your order has been authorized";
                }
                    break;
                case "PAID":
                {
                    //mark order as paid
                    if (_orderProcessingService.CanMarkOrderAsPaid(order) && status.ToUpper() == "PAID")
                            await _orderProcessingService.MarkOrderAsPaidAsync(order);
                }
                    break;
                default:
                {
                    return GetResponse("Unsupported status");
                }
            }

            return GetResponse(textToResponse, true);
        }

        public async Task<IActionResult> ConfirmPay(IFormCollection form)
        {
            var processor =
              await _paymentPluginManager.LoadPluginBySystemNameAsync("Payments.Uniteller") as UnitellerPaymentProcessor;
            if (processor == null ||
                !_paymentPluginManager.IsPluginActive(processor) || !processor.PluginDescriptor.Installed)
                throw new NopException("Uniteller module cannot be loaded");

            const string orderIdKey = "Order_ID";
            const string signatureKey = "Signature";
            const string statuskey = "Status";

            var orderId = GetValue(orderIdKey, form);
            var signature = GetValue(signatureKey, form);
            var status = GetValue(statuskey, form);

            Order order = null;

            if (Guid.TryParse(orderId, out Guid orderGuid))
            {
                order = await _orderService.GetOrderByGuidAsync(orderGuid);
            }

            if (order == null)
                return GetResponse("Order cannot be loaded");

            var sb = new StringBuilder();
            sb.AppendLine("Uniteller:");
            sb.AppendLine(orderIdKey + ": " + orderId);
            sb.AppendLine(signatureKey + ": " + signature);
            sb.AppendLine(statuskey + ": " + status);

            //order note
            await _orderService.InsertOrderNoteAsync(new OrderNote
            {
                Note = sb.ToString(),
                DisplayToCustomer = false,
                CreatedOnUtc = DateTime.UtcNow
            });

            //load settings for a chosen store scope
            var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var setting = await _settingService.LoadSettingAsync<UnitellerPaymentSettings>(storeScope);

            var checkDataString = UnitellerPaymentProcessor.GetMD5(orderId + status + setting.Password).ToUpper();

            return checkDataString != signature ? GetResponse("Invalid order data") : await UpdateOrderStatus(order, status);
        }

        public async Task<IActionResult> Success()
        {
            var orderId = _webHelper.QueryString<string>("Order_ID");
            Order order = null;

            if (Guid.TryParse(orderId, out Guid orderGuid))
            {
                order = await _orderService.GetOrderByGuidAsync(orderGuid);
            }

            if (order == null)
                return RedirectToAction("Index", "Home", new { area = string.Empty });

            //update payment status if need
            if (order.PaymentStatus != PaymentStatus.Paid)
            {
                var processor =
                  await _paymentPluginManager.LoadPluginBySystemNameAsync("Payments.Uniteller") as UnitellerPaymentProcessor;
                if (processor == null ||
                    !_paymentPluginManager.IsPluginActive(processor) || !processor.PluginDescriptor.Installed)
                    throw new NopException("Uniteller module cannot be loaded");

                var statuses = processor.GetPaymentStatus(orderId);

                foreach (var status in statuses)
                {
                    await UpdateOrderStatus(order, status);
                }
            }

            return RedirectToRoute("CheckoutCompleted", new { orderId = order.Id });
        }

        public async Task<IActionResult> CancelOrder()
        {
            var orderId = _webHelper.QueryString<string>("Order_ID");
            Order order = null;

            if (Guid.TryParse(orderId, out Guid orderGuid))
            {
                order = await _orderService.GetOrderByGuidAsync(orderGuid);
            }

            if (order == null)
                return RedirectToAction("Index", "Home", new {area = string.Empty});

            return RedirectToRoute("OrderDetails", new { orderId = order.Id });
        }
    }
}
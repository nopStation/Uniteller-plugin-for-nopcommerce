using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Nop.Web.Framework.Mvc.Routing;

namespace Nop.Plugin.Payments.Uniteller
{
    public partial class RouteProvider : IRouteProvider
    {
        public void RegisterRoutes(IEndpointRouteBuilder endpointRouteBuilder)
        {
            //confirm pay
            endpointRouteBuilder.MapControllerRoute("Plugin.Payments.Uniteller.ConfirmPay",
                 "Plugins/Uniteller/ConfirmPay",
                 new { controller = "PaymentUniteller", action = "ConfirmPay" });
            //cancel
            endpointRouteBuilder.MapControllerRoute("Plugin.Payments.Uniteller.CancelOrder",
                 "Plugins/Uniteller/CancelOrder",
                 new { controller = "PaymentUniteller", action = "CancelOrder" });
            //success
            endpointRouteBuilder.MapControllerRoute("Plugin.Payments.Uniteller.Success",
                 "Plugins/Uniteller/Success",
                 new { controller = "PaymentUniteller", action = "Success" });
        }

        public int Priority
        {
            get
            {
                return 0;
            }
        }
    }
}

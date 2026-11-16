using Microsoft.AspNetCore.Mvc;
using ZimraEGS.Models;
using ZimraEGS.Services;

namespace ZimraEGS.Controllers
{
    /// <summary>The privacy notice: what personal data this service holds, why, for how long, and customers' rights.</summary>
    [Route("privacy")]
    public class PrivacyController : Controller
    {
        private readonly PrivacyService _privacy;
        private readonly BillingService _billing;

        public PrivacyController(PrivacyService privacy, BillingService billing)
        {
            _privacy = privacy;
            _billing = billing;
        }

        [HttpGet("")]
        public IActionResult Index(bool deleted = false)
        {
            ViewData["Deleted"] = deleted;
            return View(new PrivacyPageModel { Options = _privacy.Options, Billing = _billing.Options, Missing = _privacy.MissingSettings() });
        }
    }
}

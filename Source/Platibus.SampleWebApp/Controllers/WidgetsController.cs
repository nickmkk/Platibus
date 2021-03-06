﻿using System.Threading.Tasks;
using System.Web.Mvc;
using Platibus.IIS;
using Platibus.SampleMessages.Widgets;
using Platibus.Security;

namespace Platibus.SampleWebApp.Controllers
{
    public class WidgetsController : Controller
    {
        private WidgetsClient WidgetClient
        {
            get
            {
                var bus = HttpContext.GetBus();
                return new WidgetsClient(GetAccessToken(), bus);
            }
        }

        [HttpGet]
        public async Task<ActionResult> Index()
        {
            var model = await WidgetClient.GetWidgets();
            return View(model);
        }

        [HttpGet]
        public async Task<ActionResult> Details(string id)
        {
            var model = await WidgetClient.GetWidget(id);
            return View(model);
        }

        [HttpGet]
        public ActionResult Create()
        {
            var model = new WidgetResource
            {
                Attributes = new WidgetAttributes()
            };
            return View(model);
        }

        [HttpPost]
        [SubmitAction("action", "Create")]
        public async Task<ActionResult> Create(WidgetResource model)
        {
            try
            {
                await WidgetClient.CreateWidget(model);
                return RedirectToAction("Index");
            }
            catch
            {
                return View();
            }
        }

        [HttpPost]
        [SubmitAction("action", "Create (Async)")]
        public async Task<ActionResult> CreateAsync(WidgetResource model)
        {
            try
            {
                await WidgetClient.CreateWidgetAsync(model);
                return RedirectToAction("Index");
            }
            catch
            {
                return View("Create");
            }
        }

        [HttpGet]
        public async Task<ActionResult> Edit(string id)
        {
            var model = await WidgetClient.GetWidget(id);
            return View(model);
        }

        [HttpPost]
        public async Task<ActionResult> Edit(string id, WidgetResource model)
        {
            try
            {
                await WidgetClient.UpdateWidget(id, model);
                return RedirectToAction("Index");
            }
            catch
            {
                return View();
            }
        }

        [HttpGet]
        public async Task<ActionResult> Delete(string id)
        {
            var model = await WidgetClient.GetWidget(id);
            
            return View(model);
        }

        [HttpPost]
        public async Task<ActionResult> Delete(string id, WidgetResource model)
        {
            try
            {
                await WidgetClient.DeleteWidget(id);
                return RedirectToAction("Index");
            }
            catch
            {
                return View();
            }
        }
        
        private string GetAccessToken()
        {
            // The name of the claim containing the access token may vary depending on the
            // callback registered with the SecurityTokenValidated notification in the
            // OpenIdConnectAuthentication middleware.  See the OnSecurityTokenValidated
            // callback method the Startup class.
            return HttpContext.User.GetClaimValue("access_token");
        }
    }
}

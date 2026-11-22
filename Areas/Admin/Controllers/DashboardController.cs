using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MobileShop.Data;
using MobileShop.Models;
using MobileShop.Services;
using System.Threading.Tasks;

namespace MobileShop.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class DashboardController : Controller
    {
        private readonly IReportService _reportService;
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public DashboardController(
            IReportService reportService,
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager)
        {
            _reportService = reportService;
            _context = context;
            _userManager = userManager;
        }

        public async Task<IActionResult> Index()
        {
            var dashboardData = await _reportService.GetDashboardDataAsync();
            return View(dashboardData);
        }
    }
}
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MobileShop.Models;
using MobileShop.Services;
using MobileShop.ViewModels;

namespace MobileShop.Controllers
{
    public class OrdersController : Controller
    {
        private readonly IOrderService _orderService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IShoppingCartService _cartService;
        public OrdersController(IOrderService orderService, UserManager<ApplicationUser> userManager, IShoppingCartService cartService)
        {
            _orderService = orderService;
            _userManager = userManager;
            _cartService = cartService;
        }   
    
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Index()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return NotFound();

            var orders = await _orderService.GetUserOrdersAsync(user.Id);
            ViewBag.CartItemCount = await _cartService.GetCartItemCountAsync();

            return View(orders);
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> OrderDetails(int id)
        {
            var order = await _orderService.GetOrderByIdAsync(id);
            if (order == null)
                return NotFound();

            ViewBag.CartItemCount = await _cartService.GetCartItemCountAsync();
            return View(order);
        }

        public async Task<IActionResult> TrackOrder(string? orderNumber)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
            {
                return View();
            }

            var order = await _orderService.GetOrderByNumberAsync(orderNumber);
            if (order == null)
            {
                TempData["Error"] = "Order not found.";
                return View();
            }

            return View(order);
        }
    }
}

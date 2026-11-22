using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using MobileShop.Models;
using MobileShop.Services;
using MobileShop.ViewModels;

public class CheckoutController : Controller
{
    private readonly IShoppingCartService _cartService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly IOrderService _orderService;
    private readonly IEmailService _emailService;
    private readonly Func<TwoCTwoPService> _paymentService;
    private readonly ILogger<CheckoutController> _logger;

    public CheckoutController(
        IShoppingCartService cartService,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        IOrderService orderService,
        IEmailService emailService,
        Func<TwoCTwoPService> paymentService,
        ILogger<CheckoutController> logger)
    {
        _cartService = cartService;
        _userManager = userManager;
        _configuration = configuration;
        _orderService = orderService;
        _emailService = emailService;
        _paymentService = paymentService;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var cart = await _cartService.GetCartAsync();
        if (cart.CartItems.Count == 0)
        {
            TempData["Error"] = "Your cart is empty.";
            return RedirectToAction("Index", "ShoppingCart");
        }

        var user = await _userManager.GetUserAsync(User);
        var model = new CheckoutViewModel
        {
            Cart = cart,
            IsAuthenticated = user != null,
            FirstName = user?.FirstName ?? "",
            LastName = user?.LastName ?? "",
            Email = user?.Email ?? "",
            Phone = user?.PhoneNumber ?? "",
            Address = user?.Address ?? "",
            City = user?.City ?? "",
            PostalCode = user?.PostalCode ?? "",
            Country = user?.Country ?? "Myanmar"
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Process(CheckoutViewModel model)
    {
        var cart = await _cartService.GetCartAsync();
        if (cart.CartItems.Count == 0)
        {
            TempData["Error"] = "Your cart is empty.";
            return RedirectToAction("Index", "ShoppingCart");
        }

        model.Cart = cart;
        model.IsAuthenticated = User.Identity?.IsAuthenticated == true;
        if (cart.CartItems.Any(item => item.Quantity <= 0 || item.UnitPrice < 0 || item.Quantity > item.StockQuantity) || cart.GrandTotal <= 0)
        {
            ModelState.AddModelError("", "Your cart contains invalid quantities or unavailable products. Please update it before checkout.");
        }
        if (model.PaymentMethod != PaymentMethod.TwoC2P && model.PaymentMethod != PaymentMethod.CashOnDelivery)
        {
            ModelState.AddModelError(nameof(model.PaymentMethod), "Please select an available payment method.");
        }
        if (!ModelState.IsValid)
        {
            return View("Index", model);
        }

        try
        {
            Uri? returnBase = null;
            TwoCTwoPService? paymentService = null;
            if (model.PaymentMethod == PaymentMethod.TwoC2P)
            {
                if (!string.Equals(_configuration["TwoC2P:CurrencyCode"], "MMK", StringComparison.Ordinal) ||
                    !Uri.TryCreate(_configuration["TwoC2P:ReturnBaseUrl"], UriKind.Absolute, out returnBase) ||
                    returnBase.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(returnBase.UserInfo) ||
                    !string.IsNullOrEmpty(returnBase.Query) || !string.IsNullOrEmpty(returnBase.Fragment))
                {
                    throw new InvalidOperationException("2C2P requires MMK and a configured HTTPS return base URL.");
                }

                paymentService = _paymentService();
            }

            var user = await _userManager.GetUserAsync(User);
            var order = await _orderService.CreateOrderAsync(model, user?.Id);

            if (model.PaymentMethod == PaymentMethod.CashOnDelivery)
            {
                await _cartService.ClearCartAsync();
                await _emailService.SendOrderConfirmationAsync(model.Email, order.OrderNumber, order.TotalAmount);
                return RedirectToAction(nameof(Confirmation), new { orderNumber = order.OrderNumber });
            }

            var sessionKey = PaymentSessionKey(order.Id);
            HttpContext.Session.SetString(sessionKey, model.Email);
            HttpContext.Session.SetString(sessionKey + ":CartId", HttpContext.Session.GetString("CartId") ?? "");
            var returnPath = Url.Action(nameof(PaymentReturn), "Checkout", new { orderNumber = order.OrderNumber })!;
            var frontendReturnUrl = new Uri(returnBase!, returnPath).AbsoluteUri;
            var backendReturnUrl = _configuration["TwoC2P:BackendReturnUrl"];
            var result = await paymentService!.CreatePaymentTokenAsync(
                order.OrderNumber, order.TotalAmount, frontendReturnUrl,
                string.IsNullOrWhiteSpace(backendReturnUrl) ? null : backendReturnUrl);

            if (result.RespCode != "0000" || string.IsNullOrWhiteSpace(result.WebPaymentUrl))
            {
                _logger.LogWarning("2C2P did not create a payment token for order {OrderId}: {ResponseCode}", order.Id, result.RespCode);
                ModelState.AddModelError("", "Unable to start payment. Please try again.");
                return View("Index", model);
            }

            return Redirect(result.WebPaymentUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to start checkout");
            ModelState.AddModelError("", "Unable to start checkout. Please try again or contact support.");
            return View("Index", model);
        }
    }

    [AcceptVerbs("GET", "POST")]
    [IgnoreAntiforgeryToken]
    public IActionResult PaymentReturn(string orderNumber)
    {
        return RedirectToAction(nameof(CheckPayment), new { orderNumber });
    }

    [HttpGet]
    public async Task<IActionResult> CheckPayment(string orderNumber)
    {
        var order = await _orderService.GetOrderByNumberAsync(orderNumber);
        if (order == null || order.PaymentMethod != PaymentMethod.TwoC2P)
        {
            return NotFound();
        }

        var sessionKey = PaymentSessionKey(order.Id);
        var email = HttpContext.Session.GetString(sessionKey);
        if (order.PaymentStatus == PaymentStatus.Pending)
        {
            try
            {
                var result = await _paymentService().InquireAsync(order.OrderNumber);
                await ApplyPaymentResultAsync(result);
                order = await _orderService.GetOrderByNumberAsync(orderNumber) ?? order;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not verify 2C2P payment for order {OrderId}", order.Id);
            }
        }

        if (string.IsNullOrEmpty(email))
        {
            if (order.PaymentStatus == PaymentStatus.Paid)
                TempData["Success"] = "Payment was confirmed, but your checkout session expired. Contact the shop with your order number.";
            else
                TempData["Error"] = "Your checkout session expired and payment is not confirmed. Contact the shop with your order number before paying again.";
            return RedirectToAction("Index", "Home");
        }

        if (order.PaymentStatus == PaymentStatus.Paid && HttpContext.Session.GetString(sessionKey + ":Completed") != "1")
        {
            if (HttpContext.Session.GetString(sessionKey + ":CartId") == HttpContext.Session.GetString("CartId"))
            {
                var cart = await _cartService.GetCartAsync();
                foreach (var orderItem in order.OrderItems)
                {
                    var cartItem = cart.CartItems.FirstOrDefault(item => item.ProductId == orderItem.ProductId);
                    if (cartItem != null)
                    {
                        await _cartService.UpdateCartItemAsync(cartItem.CartItemId, cartItem.Quantity - orderItem.Quantity);
                    }
                }
            }

            HttpContext.Session.SetString(sessionKey + ":Completed", "1");
            await _emailService.SendOrderConfirmationAsync(email, order.OrderNumber, order.TotalAmount);
        }

        return View("Confirmation", new OrderConfirmationViewModel
        {
            Order = order,
            OrderItems = order.OrderItems.ToList()
        });
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> PaymentNotification([FromBody] TwoCTwoPNotification? notification)
    {
        if (string.IsNullOrWhiteSpace(notification?.Payload))
        {
            return BadRequest();
        }

        TwoCTwoPPaymentResult result;
        try
        {
            result = _paymentService().VerifyBackendPayload(notification.Payload);
        }
        catch (Exception ex) when (ex is SecurityTokenException or InvalidDataException or ArgumentException or FormatException or JsonException)
        {
            _logger.LogWarning(ex, "Invalid 2C2P notification");
            return BadRequest();
        }

        return await ApplyPaymentResultAsync(result) ? Ok() : BadRequest();
    }

    public async Task<IActionResult> Confirmation(string orderNumber)
    {
        var order = await _orderService.GetOrderByNumberAsync(orderNumber);
        if (order == null)
        {
            return NotFound();
        }

        if (order.PaymentMethod == PaymentMethod.TwoC2P)
        {
            return RedirectToAction(nameof(CheckPayment), new { orderNumber });
        }

        return View(new OrderConfirmationViewModel
        {
            Order = order,
            OrderItems = order.OrderItems.ToList()
        });
    }

    private async Task<bool> ApplyPaymentResultAsync(TwoCTwoPPaymentResult result)
    {
        if (result.InvoiceNo.Length != 12 || result.InvoiceNo.Any(character => !char.IsAsciiDigit(character)) ||
            result.ReferenceNo.Length > 50)
        {
            return false;
        }

        var order = await _orderService.GetOrderByNumberAsync(result.InvoiceNo);
        if (order == null || order.PaymentMethod != PaymentMethod.TwoC2P || order.TotalAmount != result.Amount ||
            result.CurrencyCode != "MMK" || _configuration["TwoC2P:CurrencyCode"] != "MMK")
        {
            return false;
        }

        if (result.RespCode != "0000")
        {
            return true;
        }

        if (order.PaymentStatus == PaymentStatus.Paid)
        {
            return order.TransactionId == result.ReferenceNo;
        }

        if (order.PaymentStatus != PaymentStatus.Pending)
        {
            return false;
        }

        if (await _orderService.ProcessTwoC2PPaymentAsync(order.Id, result.ReferenceNo))
        {
            return true;
        }

        order = await _orderService.GetOrderByNumberAsync(result.InvoiceNo);
        return order?.PaymentStatus == PaymentStatus.Paid && order.TransactionId == result.ReferenceNo;
    }

    private static string PaymentSessionKey(int orderId) => $"TwoC2P:{orderId}";
}

public record TwoCTwoPNotification(string Payload);

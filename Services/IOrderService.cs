using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MobileShop.Data;
using MobileShop.Models;
using MobileShop.ViewModels;

namespace MobileShop.Services
{
    public interface IOrderService
    {
        Task<Order> CreateOrderAsync(CheckoutViewModel model, string? userId);
        Task<Order?> GetOrderByNumberAsync(string orderNumber);
        Task<bool> UpdateOrderStatusAsync(int orderId, OrderStatus status);
        Task<bool> ProcessTwoC2PPaymentAsync(int orderId, string transactionId);
    }

    public class OrderService : IOrderService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<OrderService> _logger;

        public OrderService(ApplicationDbContext context, ILogger<OrderService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<Order> CreateOrderAsync(CheckoutViewModel model, string? userId)
        {
            await using var transaction = model.PaymentMethod == PaymentMethod.CashOnDelivery
                ? await _context.Database.BeginTransactionAsync()
                : null;

            var order = new Order
            {
                UserId = userId,
                Subtotal = model.Cart.CartTotal,
                TaxAmount = model.Cart.Tax,
                ShippingCost = model.Cart.Shipping,
                TotalAmount = model.Cart.GrandTotal,
                PaymentMethod = model.PaymentMethod,
                ShippingAddress = model.Address,
                ShippingCity = model.City,
                ShippingPostalCode = model.PostalCode,
                ShippingCountry = model.Country,
                ShippingPhone = model.Phone,
                Notes = model.OrderNotes
            };

            if (model.PaymentMethod == PaymentMethod.TwoC2P)
            {
                order.OrderNumber = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture) +
                                    RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6", CultureInfo.InvariantCulture);
            }

            foreach (var item in model.Cart.CartItems)
            {
                order.OrderItems.Add(new OrderItem
                {
                    ProductId = item.ProductId,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice
                });

                if (model.PaymentMethod == PaymentMethod.CashOnDelivery)
                {
                    var updated = await _context.Products
                        .Where(product => product.Id == item.ProductId && product.StockQuantity >= item.Quantity)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(
                            product => product.StockQuantity, product => product.StockQuantity - item.Quantity));
                    if (updated == 0)
                    {
                        throw new InvalidOperationException("A product is no longer available in the requested quantity.");
                    }
                }
            }

            _context.Orders.Add(order);
            await _context.SaveChangesAsync();
            if (transaction != null)
            {
                await transaction.CommitAsync();
            }

            _logger.LogInformation($"Order {order.OrderNumber} created successfully");
            return order;
        }

        public async Task<bool> UpdateOrderStatusAsync(int orderId, OrderStatus status)
        {
            var order = await _context.Orders.FindAsync(orderId);
            if (order == null || (order.PaymentMethod == PaymentMethod.TwoC2P && status == OrderStatus.Refunded)) return false;

            order.Status = status;

            if (status == OrderStatus.Shipped)
            {
                order.ShippedDate = DateTime.Now;
            }
            else if (status == OrderStatus.Delivered)
            {
                order.DeliveredDate = DateTime.Now;

                // FIX: Mark payment as Paid for COD and manual payment methods on delivery
                if (order.PaymentStatus == PaymentStatus.Pending &&
                    (order.PaymentMethod == PaymentMethod.CashOnDelivery ||
                     order.PaymentMethod == PaymentMethod.UPI ||
                     order.PaymentMethod == PaymentMethod.CreditCard ||
                     order.PaymentMethod == PaymentMethod.DebitCard))
                {
                    order.PaymentStatus = PaymentStatus.Paid;
                }
            }
            else if (status == OrderStatus.Refunded)
            {
                // FIX: When order is refunded, payment status must also be Refunded
                order.PaymentStatus = PaymentStatus.Refunded;
            }
            else if (status == OrderStatus.Cancelled)
            {
                // FIX: If order was already paid and then cancelled, mark payment as Refunded
                if (order.PaymentStatus == PaymentStatus.Paid && order.PaymentMethod != PaymentMethod.TwoC2P)
                {
                    order.PaymentStatus = PaymentStatus.Refunded;
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation($"Order {orderId} status updated to {status}");
            return true;
        }

        public async Task<bool> ProcessTwoC2PPaymentAsync(int orderId, string transactionId)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            var updated = await _context.Orders
                .Where(o => o.Id == orderId && o.PaymentMethod == PaymentMethod.TwoC2P &&
                            o.PaymentStatus == PaymentStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(o => o.PaymentStatus, PaymentStatus.Paid)
                    .SetProperty(o => o.TransactionId, transactionId));

            if (updated == 0)
            {
                return false;
            }

            var items = await _context.OrderItems.AsNoTracking()
                .Where(item => item.OrderId == orderId)
                .ToListAsync();
            foreach (var item in items)
            {
                await _context.Products.Where(product => product.Id == item.ProductId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        product => product.StockQuantity, product => product.StockQuantity - item.Quantity));
                if (await _context.Products.AnyAsync(product => product.Id == item.ProductId && product.StockQuantity < 0))
                {
                    _logger.LogWarning("Paid order {OrderId} requires stock reconciliation for product {ProductId}", orderId, item.ProductId);
                }
            }

            await transaction.CommitAsync();
            _logger.LogInformation("Payment processed for order {OrderId}", orderId);
            return true;
        }

        public async Task<Order?> GetOrderByNumberAsync(string orderNumber)
        {
            return await _context.Orders.AsNoTracking()
                .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Product)
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
        }
    }
}
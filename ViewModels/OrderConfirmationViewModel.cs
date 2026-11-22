using System.Collections.Generic;
using MobileShop.Models;

namespace MobileShop.ViewModels
{
    public class OrderConfirmationViewModel
    {
        public Order Order { get; set; } = null!;
        public List<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
    }
}
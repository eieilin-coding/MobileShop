using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MobileShop.Data;
using MobileShop.Models;
using MobileShop.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Database Context
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Identity Configuration
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedEmail = false;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.AddPolicy("CustomerOnly", policy => policy.RequireRole("Customer"));
});

// Session & Shopping Cart
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Register Custom Services for Dependency Injection
builder.Services.AddScoped<IProductService, ProductService>();

// 1. Add HttpContextAccessor to access Session inside the Service layer to buy for guest user without login
builder.Services.AddHttpContextAccessor();

// 2. Enable Session State Services
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30); // Cart expires after 30 mins of inactivity
    options.HttpOnly = true;                        // Security: Prevents XSS attacks from reading session cookie
    options.IsEssential = true;                     // Essential for cart functionality to bypass GDPR cookie consent
});

// 3. Register Custom Application Services
//builder.Services.AddScoped<IShoppingCartService, MobileShop.Services.ShoppingCartService>();
builder.Services.AddScoped<IShoppingCartService,ShoppingCartService>();


// AutoMapper
builder.Services.AddAutoMapper(typeof(Program));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// must place app.UseSession() after routing (app.UseRouting()) but before authorization parameters (app.UseAuthorization()). If it is misplaced,
// the session data will not populate before your controller actions trigger!

app.UseRouting();
app.UseSession();

app.UseAuthentication();
app.UseAuthorization();

// Seed Database  for only one time
//using (var scope = app.Services.CreateScope())
//{
//    var services = scope.ServiceProvider;
//    await DbInitializer.SeedAsync(services);
//}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// --- Down further in the file, right before app.Run() ---

// 4. Activate the Session Middleware (Must be called before Authorization)
app.UseSession();

app.Run();
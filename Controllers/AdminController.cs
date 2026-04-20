using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ASM.Data;
using ASM.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ASM.Controllers
{

    public class TopProductVM
    {
        public string? ProductName { get; set; }
        public int QuantitySold { get; set; }
        public decimal Revenue { get; set; }
    }

    [Authorize(Roles = "Admin,Staff")]
    public class AdminController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _hostEnvironment;

        public AdminController(ApplicationDbContext context, IWebHostEnvironment hostEnvironment)
        {
            _context = context;
            _hostEnvironment = hostEnvironment;
        }

        // ===== TRANG CHÍNH =====
        public IActionResult Index()
        {
            return RedirectToAction("Report");
        }

        // ===== BÁO CÁO THỐNG KÊ =====
        public async Task<IActionResult> Report(string filter = "month")
        {
            var now = DateTime.Now;
            DateTime startDate;
            DateTime endDate = now.AddDays(1).Date;

            switch (filter.ToLower())
            {
                case "week":
                    int diff = (7 + (now.DayOfWeek - DayOfWeek.Monday)) % 7;
                    startDate = now.Date.AddDays(-1 * diff);
                    break;
                case "year":
                    startDate = new DateTime(now.Year, 1, 1);
                    break;
                case "month":
                default:
                    startDate = new DateTime(now.Year, now.Month, 1);
                    break;
            }

            var ordersPeriod = await _context.Orders
                .Where(d => d.CreatedAt >= startDate && d.CreatedAt < endDate)
                .ToListAsync();

            // CHUẨN HÓA TRẠNG THÁI: 4 là Hoàn thành, 5 là Hủy, 1,2,3 là Đang xử lý
            var successOrders = ordersPeriod.Where(d => d.OrderStatus == 4).ToList();
            var pendingOrders = ordersPeriod.Where(d => d.OrderStatus >= 1 && d.OrderStatus <= 3).ToList();
            var cancelledOrders = ordersPeriod.Where(d => d.OrderStatus >= 5).ToList();

            ViewBag.TotalRevenue = successOrders.Sum(d => d.FinalAmount);
            ViewBag.TotalOrders = ordersPeriod.Count;
            ViewBag.SuccessOrders = successOrders.Count;
            ViewBag.PendingOrders = pendingOrders.Count;
            ViewBag.CancelledOrders = cancelledOrders.Count;

            List<decimal> revenueData = new List<decimal>();
            List<string> chartLabels = new List<string>();

            if (filter == "year")
            {
                decimal[] yearData = new decimal[12];
                foreach (var order in successOrders) { yearData[order.CreatedAt.Month - 1] += order.FinalAmount; }
                revenueData = yearData.ToList();
                for (int i = 1; i <= 12; i++) chartLabels.Add($"Tháng {i}");
            }
            else if (filter == "week")
            {
                decimal[] weekData = new decimal[7];
                string[] daysOfWeek = { "Thứ 2", "Thứ 3", "Thứ 4", "Thứ 5", "Thứ 6", "Thứ 7", "CN" };
                foreach (var order in successOrders)
                {
                    int dayIndex = (7 + (order.CreatedAt.DayOfWeek - DayOfWeek.Monday)) % 7;
                    weekData[dayIndex] += order.FinalAmount;
                }
                revenueData = weekData.ToList();
                chartLabels = daysOfWeek.ToList();
            }
            else
            {
                int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
                decimal[] monthData = new decimal[daysInMonth];
                foreach (var order in successOrders)
                {
                    monthData[order.CreatedAt.Day - 1] += order.FinalAmount;
                }
                revenueData = monthData.ToList();
                for (int i = 1; i <= daysInMonth; i++) chartLabels.Add($"Mùng {i}");
            }

            ViewBag.DailyRevenue = revenueData;
            ViewBag.ChartLabels = chartLabels;
            ViewBag.CurrentMonth = now.ToString("MM/yyyy");

            // ĐÃ SỬA: Tìm các sản phẩm từ các đơn hàng có OrderStatus == 4 (Hoàn Thành)
            var topProducts = await _context.OrderDetails
                .Include(c => c.Product)
                .Include(c => c.Order)
                .Where(c => c.Order.CreatedAt >= startDate && c.Order.CreatedAt < endDate && c.Order.OrderStatus == 4)
                .GroupBy(c => new { c.ProductId, c.Product.ProductName })
                .Select(g => new TopProductVM
                {
                    ProductName = g.Key.ProductName,
                    QuantitySold = g.Sum(x => x.Quantity),
                    Revenue = g.Sum(x => x.Quantity * x.Price)
                })
                .OrderByDescending(x => x.QuantitySold)
                .Take(5)
                .ToListAsync();

            ViewBag.TopProducts = topProducts;
            return View();
        }

        // ===== QUẢN LÝ NGƯỜI DÙNG =====
        [ActionName("User")]
        public async Task<IActionResult> AppUser()
        {
            // Lấy danh sách user theo ID
            var users = await _context.Users.OrderBy(u => u.UserId).ToListAsync();
            return View("User", users);
        }

        [HttpPost]
        public async Task<IActionResult> CreateUser(string fullName, string email, string password, string? phone, string? address, int role, IFormFile? avatar)
        {
            // Validate bắt buộc
            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                TempData["Error"] = "Lỗi: Họ tên, Email và Mật khẩu không được để trống!";
                return RedirectToAction("User");
            }

            // Regex: chỉ cho phép chữ + khoảng trắng
            if (!System.Text.RegularExpressions.Regex.IsMatch(fullName, @"^[\p{L}\s]+$"))
            {
                TempData["Error"] = "Lỗi: Họ tên không hợp lệ!";
                return RedirectToAction("User");
            }

            // Kiểm tra email trùng
            bool emailExists = await _context.Users.AnyAsync(u => u.Email.ToLower() == email.ToLower());
            if (emailExists)
            {
                TempData["Error"] = "Lỗi: Email đã tồn tại!";
                return RedirectToAction("User");
            }

            // Tạo user mới
            var user = new User
            {
                FullName = fullName.Trim(),
                Email = email.Trim().ToLower(), // chuẩn hóa email
                Password = password, // ⚠ chưa mã hóa (lỗ hổng bảo mật)
                Phone = phone,
                Address = address,
                Role = role, // 2: Admin, 1: Staff, 0: User
                Status = "Active",
                CreatedAt = DateTime.Now
            };

            // Upload avatar nếu có
            if (avatar != null) user.Avatar = await SaveImageFile(avatar);

            try
            {
                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                TempData["ScrollDown"] = true;
                TempData["Success"] = "Thêm mới người dùng thành công!";
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Lỗi Database: " + ex.Message;
            }

            return RedirectToAction("User");
        }

        [HttpPost]
        public async Task<IActionResult> EditUser(int userId, string fullName, string email, string? phone, string? address, int role, string? password, IFormFile? newAvatar)
        {
            var user = await _context.Users.FindAsync(userId);

            if (user != null)
            {
                // Lấy ID người đang đăng nhập
                var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

                // Nếu sửa chính mình → cho đổi tên + password
                if (currentUserId == userId.ToString())
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(fullName, @"^[\p{L}\s]+$"))
                    {
                        TempData["Error"] = "Tên không hợp lệ!";
                        return RedirectToAction("User");
                    }

                    user.FullName = fullName;

                    if (!string.IsNullOrEmpty(password))
                        user.Password = password; // ⚠ nên mã hóa
                }

                // Cập nhật thông tin
                user.Email = email;
                user.Phone = phone;
                user.Address = address;
                user.Role = role;

                if (newAvatar != null)
                    user.Avatar = await SaveImageFile(newAvatar);

                await _context.SaveChangesAsync();

                // Nếu sửa chính mình → cập nhật lại cookie đăng nhập
                if (currentUserId == userId.ToString())
                {
                    string roleName = user.Role == 2 ? "Admin" : (user.Role == 1 ? "Staff" : "User");

                    var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.FullName ?? user.Email),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, roleName),
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString())
            };

                    var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity));
                }
            }

            return RedirectToAction("User");
        }

        [HttpPost]
        public async Task<IActionResult> ToggleUserStatus(int id)
        {
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Không cho tự khóa mình
            if (currentUserId == id.ToString())
            {
                TempData["Error"] = "Không thể tự khóa!";
                return RedirectToAction("User");
            }

            var user = await _context.Users.FindAsync(id);

            if (user != null)
            {
                // Toggle trạng thái
                user.Status = user.Status == "Locked" ? "Active" : "Locked";
                await _context.SaveChangesAsync();
            }

            return RedirectToAction("User");
        }

        [HttpPost]
        public async Task<IActionResult> DeleteUser(int id)
        {
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Không cho tự xóa
            if (currentUserId == id.ToString())
            {
                TempData["Error"] = "Không thể tự xóa!";
                return RedirectToAction("User");
            }

            var user = await _context.Users.FindAsync(id);

            if (user != null)
            {
                // Kiểm tra dữ liệu liên quan
                bool hasOrders = await _context.Orders.AnyAsync(o => o.UserId == id);
                bool hasCarts = await _context.Carts.AnyAsync(c => c.UserId == id);
                bool hasReviews = await _context.Reviews.AnyAsync(r => r.UserId == id);

                // Nếu có liên kết → không xóa, chỉ khóa (soft delete)
                if (hasOrders || hasCarts || hasReviews)
                {
                    user.Status = "Locked";
                    await _context.SaveChangesAsync();

                    TempData["Error"] = "User có dữ liệu → đã khóa!";
                }
                else
                {
                    _context.Users.Remove(user);
                    await _context.SaveChangesAsync();

                    TempData["Success"] = "Đã xóa!";
                }
            }

            return RedirectToAction("User");
        }

        // ===== QUẢN LÝ DANH MỤC =====
        public async Task<IActionResult> Category()
        {
            // Include để lấy luôn sản phẩm trong danh mục
            var categories = await _context.Categories
                .Include(c => c.Products)
                .OrderBy(c => c.CategoryId)
                .ToListAsync();

            return View("Category", categories);
        }

        [HttpPost]
        public async Task<IActionResult> CreateCategory(string categoryName, string description)
        {
            // Validate
            if (string.IsNullOrWhiteSpace(categoryName))
            {
                TempData["Error"] = "Tên không được để trống!";
                return RedirectToAction("Category");
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(categoryName, @"^[\p{L}\s]+$"))
            {
                TempData["Error"] = "Tên không hợp lệ!";
                return RedirectToAction("Category");
            }

            // Check trùng
            bool nameExists = await _context.Categories.AnyAsync(c => c.CategoryName.ToLower() == categoryName.Trim().ToLower());

            if (nameExists)
            {
                TempData["Error"] = "Tên đã tồn tại!";
                return RedirectToAction("Category");
            }

            var cat = new Category
            {
                CategoryName = categoryName.Trim(),
                Description = description
            };

            _context.Categories.Add(cat);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Thêm thành công!";
            return RedirectToAction("Category");
        }


        [HttpPost]
        public async Task<IActionResult> EditCategory(int categoryId, string categoryName, string description, string? status)
        {
            if (string.IsNullOrWhiteSpace(categoryName))
            {
                TempData["Error"] = "Lỗi: Tên danh mục không được để trống!";
                return RedirectToAction("Category");
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(categoryName, @"^[\p{L}\s]+$"))
            {
                TempData["Error"] = "Lỗi: Tên danh mục không được chứa số hoặc ký tự đặc biệt!";
                return RedirectToAction("Category");
            }

            var cat = await _context.Categories.FindAsync(categoryId);
            if (cat != null)
            {
                bool nameExists = await _context.Categories.AnyAsync(c => c.CategoryId != categoryId && c.CategoryName.ToLower() == categoryName.Trim().ToLower());
                if (nameExists)
                {
                    TempData["Error"] = "Lỗi: Tên danh mục đã tồn tại trong hệ thống!";
                    return RedirectToAction("Category");
                }

                cat.CategoryName = categoryName.Trim();
                cat.Description = description;
                if (!string.IsNullOrEmpty(status)) cat.Status = status;

                await _context.SaveChangesAsync();
            }
            return RedirectToAction("Category");
        }

        [HttpPost]
        public async Task<IActionResult> ToggleCategoryStatus(int id)
        {
            var cat = await _context.Categories.FindAsync(id);
            if (cat != null)
            {
                cat.Status = cat.Status == "Active" ? "Hidden" : "Active";
                await _context.SaveChangesAsync();
            }
            return RedirectToAction("Category");
        }

        [HttpPost]
        public async Task<IActionResult> DeleteCategory(int id)
        {
            var cat = await _context.Categories.Include(c => c.Products).FirstOrDefaultAsync(c => c.CategoryId == id);
            if (cat != null)
            {
                if (cat.Products != null && cat.Products.Any())
                {
                    TempData["Error"] = $"Không thể xóa danh mục '{cat.CategoryName}' vì đang có {cat.Products.Count} sản phẩm thuộc danh mục này! Vui lòng xóa hoặc chuyển sản phẩm sang danh mục khác trước.";
                    return RedirectToAction("Category");
                }

                _context.Categories.Remove(cat);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa danh mục thành công!";
            }
            return RedirectToAction("Category");
        }

        // ===== QUẢN LÝ THỰC ĐƠN =====
        public async Task<IActionResult> Menu()
        {
            var products = await _context.Products.Include(p => p.Category).OrderBy(p => p.ProductId).ToListAsync();
            ViewBag.Categories = await _context.Categories.ToListAsync();
            return View("Menu", products);
        }

        [HttpPost]
        public async Task<IActionResult> CreateMenuItem(string productName, decimal price, int categoryId, string? description, IFormFile? image)
        {
            if (string.IsNullOrWhiteSpace(productName))
            {
                TempData["Error"] = "Tên món không được để trống.";
                return RedirectToAction("Menu");
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(productName, @"^[\p{L}0-9\s]+$"))
            {
                TempData["Error"] = "Lỗi: Tên món không được chứa ký tự đặc biệt!";
                return RedirectToAction("Menu");
            }

            if (price < 10)
            {
                TempData["Error"] = "Giá món phải tối thiểu là 10đ.";
                return RedirectToAction("Menu");
            }

            var product = new Product
            {
                ProductName = productName.Trim(),
                Price = price,
                CategoryId = categoryId,
                Description = description,
                Status = "Active",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            bool nameExists = await _context.Products.AnyAsync(p => p.ProductName.ToLower() == productName.Trim().ToLower());
            if (nameExists)
            {
                TempData["Error"] = "Lỗi: Tên món đã tồn tại trong hệ thống!";
                return RedirectToAction("Menu");
            }

            if (image != null) product.Image = await SaveImageFile(image);

            try
            {
                _context.Products.Add(product);
                await _context.SaveChangesAsync();

                TempData["ScrollDown"] = true;
                TempData["Success"] = "Thêm mới món thành công!";
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Lỗi Database: " + ex.Message;
            }

            return RedirectToAction("Menu");
        }

        [HttpPost]
        public async Task<IActionResult> EditMenuItem(int productId, string productName, decimal price, int categoryId, string? description, IFormFile? newImage)
        {
            if (string.IsNullOrWhiteSpace(productName))
            {
                TempData["Error"] = "Tên món không được để trống.";
                return RedirectToAction("Menu");
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(productName, @"^[\p{L}0-9\s]+$"))
            {
                TempData["Error"] = "Lỗi: Tên món không được chứa ký tự đặc biệt!";
                return RedirectToAction("Menu");
            }

            if (price < 10)
            {
                TempData["Error"] = "Giá món phải tối thiểu là 10đ.";
                return RedirectToAction("Menu");
            }

            var sp = await _context.Products.FindAsync(productId);
            if (sp != null)
            {
                bool nameExists = await _context.Products.AnyAsync(p => p.ProductId != productId && p.ProductName.ToLower() == productName.Trim().ToLower());
                if (nameExists)
                {
                    TempData["Error"] = "Lỗi: Tên món đã tồn tại trong hệ thống!";
                    return RedirectToAction("Menu");
                }

                sp.ProductName = productName.Trim();
                sp.Price = price;
                sp.CategoryId = categoryId;
                sp.Description = description;
                sp.UpdatedAt = DateTime.Now;
                if (newImage != null) sp.Image = await SaveImageFile(newImage);
                await _context.SaveChangesAsync();

                TempData["Success"] = "Sửa thông tin món thành công!";
            }
            return RedirectToAction("Menu");
        }

        [HttpPost]
        public async Task<IActionResult> ToggleMenuItemStatus(int id)
        {
            var sp = await _context.Products.FindAsync(id);
            if (sp != null)
            {
                sp.Status = sp.Status == "Active" ? "Hidden" : "Active";
                await _context.SaveChangesAsync();
            }
            return RedirectToAction("Menu");
        }



        // ===== QUẢN LÝ VOUCHER =====
        public async Task<IActionResult> Voucher()
        {
            var vouchers = await _context.Vouchers
                .Include(k => k.Orders)
                .OrderByDescending(k => k.StartDate)
                .ToListAsync();

            return View("Voucher", vouchers);
        }

        [HttpPost]
        public async Task<IActionResult> CreateVoucher(Voucher voucher)
        {
            if (!string.IsNullOrEmpty(voucher.Code))
            {
                // Ép kiểu chữ IN HOA và xóa khoảng trắng thừa
                voucher.Code = voucher.Code.Trim().ToUpper();

                bool codeExists = await _context.Vouchers.AnyAsync(v => v.Code.ToUpper() == voucher.Code);
                if (codeExists)
                {
                    TempData["Error"] = "Lỗi: Mã Voucher đã tồn tại trong hệ thống!";
                    return RedirectToAction("Voucher");
                }

                _context.Vouchers.Add(voucher);
                await _context.SaveChangesAsync();

                TempData["ScrollDown"] = true;
                TempData["Success"] = "Thêm mới Voucher thành công!";
            }
            return RedirectToAction("Voucher");
        }

        [HttpPost]
        public async Task<IActionResult> EditVoucher(Voucher voucher)
        {
            var existing = await _context.Vouchers.FindAsync(voucher.VoucherId);
            if (existing != null)
            {
                voucher.Code = voucher.Code?.Trim().ToUpper() ?? "";
                bool codeExists = await _context.Vouchers.AnyAsync(v => v.VoucherId != voucher.VoucherId && v.Code.ToUpper() == voucher.Code);
                if (codeExists)
                {
                    TempData["Error"] = "Lỗi: Mã Voucher đã tồn tại trong hệ thống!";
                    return RedirectToAction("Voucher");
                }

                existing.Code = voucher.Code;
                existing.Name = voucher.Name;
                existing.DiscountType = voucher.DiscountType;
                existing.DiscountValue = voucher.DiscountValue;
                existing.MinOrderValue = voucher.MinOrderValue;
                existing.UsageLimit = voucher.UsageLimit;
                existing.StartDate = voucher.StartDate;
                existing.EndDate = voucher.EndDate;

                await _context.SaveChangesAsync();
            }
            return RedirectToAction("Voucher");
        }

        [HttpPost]
        public async Task<IActionResult> ToggleVoucherStatus(int id)
        {
            var voucher = await _context.Vouchers.FindAsync(id);
            if (voucher != null)
            {
                voucher.Status = voucher.Status == "Active" ? "Locked" : "Active";
                await _context.SaveChangesAsync();
            }
            return RedirectToAction("Voucher");
        }

        [HttpPost]
        public async Task<IActionResult> DeleteVoucher(int id)
        {
            var voucher = await _context.Vouchers.Include(v => v.Orders).FirstOrDefaultAsync(v => v.VoucherId == id);
            if (voucher != null)
            {
                if (voucher.Orders != null && voucher.Orders.Any())
                {
                    TempData["Error"] = $"Không thể xóa voucher '{voucher.Code}' vì đã có {voucher.Orders.Count} đơn hàng sử dụng! Bạn có thể khóa voucher thay vì xóa.";
                    return RedirectToAction("Voucher");
                }

                _context.Vouchers.Remove(voucher);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Đã xóa voucher thành công!";
            }
            return RedirectToAction("Voucher");
        }

        // ===== QUẢN LÝ ĐƠN HÀNG (ĐÃ PHỤC HỒI) =====
        public async Task<IActionResult> Order() // Hoặc tên hàm của bạn
        {
            var orders = await _context.Orders
                .Include(o => o.User)
                .Include(o => o.Voucher)             // THÊM: Kéo thông tin voucher đã dùng
                .Include(o => o.OrderDetails)        // QUAN TRỌNG 1: Kéo chi tiết đơn
                    .ThenInclude(od => od.Product)   // QUAN TRỌNG 2: Kéo thông tin món ăn
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            return View(orders);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateOrderStatus(int orderId, int orderStatus)
        {
            var order = await _context.Orders.FindAsync(orderId);
            if (order != null)
            {
                order.OrderStatus = orderStatus;
                await _context.SaveChangesAsync();
            }
            return RedirectToAction("Order");
        }

        public IActionResult OrderDetail(int id)
        {
            var order = _context.Orders
                .Include(o => o.User)
                .Include(o => o.Voucher)
                .Include(o => o.OrderDetails)
                    .ThenInclude(od => od.Product)
                .FirstOrDefault(o => o.OrderId == id);

            if (order == null) return NotFound();

            return PartialView(order);
        }

        // ===== HÀM PHỤ TRỢ =====
        private async Task<string> SaveImageFile(IFormFile file)
        {
            string folder = Path.Combine(_hostEnvironment.WebRootPath, "images");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            string fileName = Guid.NewGuid().ToString() + "_" + file.FileName;
            string filePath = Path.Combine(folder, fileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(fileStream);
            }
            return fileName;
        }

        [HttpPost]
        public async Task<IActionResult> DeleteMenuItem(int id)
        {
            // Tìm sản phẩm cần xóa
            var sp = await _context.Products.FindAsync(id);

            if (sp != null)
            {
                // 1. Kiểm tra xem sản phẩm này đã từng nằm trong bất kỳ đơn hàng nào chưa
                bool hasOrders = await _context.OrderDetails.AnyAsync(od => od.ProductId == id);

                if (hasOrders)
                {
                    // 2. NẾU ĐÃ CÓ ĐƠN HÀNG: Không xóa vĩnh viễn để bảo toàn dữ liệu hóa đơn.
                    // Thay vào đó, chuyển trạng thái thành "Hidden" (Xóa mềm / Ẩn)
                    sp.Status = "Hidden";

                    // Cập nhật lại thời gian (tùy chọn)
                    sp.UpdatedAt = DateTime.Now;

                    await _context.SaveChangesAsync();

                    TempData["Success"] = "Sản phẩm này đã phát sinh đơn hàng nên không thể xóa vĩnh viễn. Hệ thống đã tự động chuyển sang trạng thái 'Ẩn' để bảo toàn dữ liệu thống kê!";
                }
                else
                {
                    // 3. NẾU CHƯA CÓ ĐƠN HÀNG NÀO: Có thể xóa vĩnh viễn an toàn khỏi Database
                    _context.Products.Remove(sp);
                    await _context.SaveChangesAsync();

                    TempData["Success"] = "Đã xóa vĩnh viễn sản phẩm thành công!";
                }
            }
            else
            {
                TempData["Error"] = "Không tìm thấy sản phẩm cần xóa!";
            }

            return RedirectToAction("Menu");
        }
    }
}
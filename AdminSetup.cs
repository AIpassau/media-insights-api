using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MediaInsights.Api;

public static class AdminSetup
{
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        await DatabaseInitializer.InitializeAsync(db, scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>().ContentRootPath);
        Console.WriteLine("管理员设置 / Admin setup（不启动 HTTP 服务）");
        Console.WriteLine($"数据库 / Database: {db.Database.GetDbConnection().DataSource}");
        Console.Write("管理员邮箱 / Admin email: ");
        var email = Console.ReadLine()?.Trim() ?? "";
        if (!new EmailAddressAttribute().IsValid(email)) return Fail("邮箱格式不正确 / Invalid email.");
        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            if (!await users.IsInRoleAsync(existing, "Admin"))
                return Fail("此邮箱属于普通用户，请使用另一个邮箱。不会自动提升普通用户权限。");
            Console.Write("管理员已存在。重设密码并恢复登录？输入 RESET 确认: ");
            if (Console.ReadLine() != "RESET") return Fail("已取消，账号未修改。");
        }
        Console.WriteLine("密码至少 12 位，包含大小写字母、数字和符号。");
        var password = ReadPassword("新密码 / New password: ");
        var confirmation = ReadPassword("再次输入 / Confirm password: ");
        if (password != confirmation) return Fail("两次密码不一致，账号未修改。");
        using var transaction = await db.Database.BeginTransactionAsync();
        if (!await roles.RoleExistsAsync("Admin"))
        {
            var roleResult = await roles.CreateAsync(new IdentityRole("Admin"));
            if (!roleResult.Succeeded) return Fail(Errors(roleResult));
        }
        var user = existing ?? new AppUser { UserName = email, Email = email, EmailConfirmed = true };
        IdentityResult result;
        if (existing is null) result = await users.CreateAsync(user, password);
        else result = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password);
        if (!result.Succeeded) return Fail(Errors(result));
        if (existing is null)
        {
            result = await users.AddToRoleAsync(user, "Admin");
            if (!result.Succeeded) return Fail(Errors(result));
        }
        else
        {
            user.Disabled = false;
            user.EmailConfirmed = true;
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;
            result = await users.UpdateAsync(user);
            if (!result.Succeeded) return Fail(Errors(result));
        }
        await transaction.CommitAsync();
        Console.WriteLine("设置成功。邮箱、密码哈希和 Admin 角色已保存到数据库。请使用刚设置的密码登录网页。");
        return 0;
    }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
    private static string Errors(IdentityResult result) => string.Join("; ", result.Errors.Select(e => e.Description));
    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}

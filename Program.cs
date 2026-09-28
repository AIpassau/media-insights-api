using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using System.Text;
using MediaInsights.Api;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

var setupAdmin = args.Contains("--setup-admin");
var initializeDatabase = args.Contains("--initialize-database");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--setup-admin" && a != "--initialize-database").ToArray());
builder.Services.AddDbContext<AppDbContext>(o =>
    DatabaseConfiguration.Configure(o, builder.Configuration));
builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
{
    o.User.RequireUniqueEmail = true;
    o.SignIn.RequireConfirmedEmail = true;
    o.Password.RequiredLength = 12;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "media-insights-session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = async c =>
    {
        var manager = c.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await manager.GetUserAsync(c.Principal!);
        if (user is null || user.Disabled ||
            c.Principal!.FindFirstValue(manager.Options.ClaimsIdentity.SecurityStampClaimType) != await manager.GetSecurityStampAsync(user))
        { c.RejectPrincipal(); return; }
        c.ReplacePrincipal(await c.HttpContext.RequestServices.GetRequiredService<IUserClaimsPrincipalFactory<AppUser>>().CreateAsync(user));
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o => o.HeaderName = "X-XSRF-TOKEN");
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("account", context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.RejectionStatusCode = 429;
});
builder.Services.AddScoped<IAppMailSender, AppMailSender>();
var app = builder.Build();
if (initializeDatabase)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseInitializer.InitializeAsync(db, app.Environment.ContentRootPath);
    Console.WriteLine($"Database initialized ({db.Database.ProviderName}).");
    await app.DisposeAsync();
    return;
}
if (setupAdmin)
{
    Environment.ExitCode = await AdminSetup.RunAsync(app.Services);
    await app.DisposeAsync();
    return;
}
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
        !HttpMethods.IsOptions(context.Request.Method))
    {
        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid CSRF token." });
            return;
        }
    }
    await next();
});
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseInitializer.InitializeAsync(db, app.Environment.ContentRootPath);
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync("Admin")) await roles.CreateAsync(new IdentityRole("Admin"));
    var email = app.Configuration["Admin:Email"];
    var password = app.Configuration["Admin:Password"];
    if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password))
    {
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByEmailAsync(email) is null)
        {
            var admin = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await users.CreateAsync(admin, password);
            if (!created.Succeeded) throw new InvalidOperationException(
                "Admin creation failed: " + string.Join("; ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(admin, "Admin");
        }
    }
}
app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
{
    var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
    return Results.Ok(new { token });
});
var auth = app.MapGroup("/api/auth").RequireRateLimiting("account");
auth.MapPost("/register", async Task<IResult> (RegisterRequest request, HttpContext http, UserManager<AppUser> users, IAppMailSender mail) =>
{
    if (!new EmailAddressAttribute().IsValid(request.Email)) return Results.BadRequest(new { error = "Invalid email." });
    var email = request.Email.Trim();
    var user = new AppUser { UserName = email, Email = email };
    var result = await users.CreateAsync(user, request.Password);
    if (!result.Succeeded) return Results.BadRequest(new { error = string.Join(" ", result.Errors.Select(e => e.Description)) });
    var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));
    var link = $"{http.Request.Scheme}://{http.Request.Host}/api/auth/confirm?userId={Uri.EscapeDataString(user.Id)}&code={Uri.EscapeDataString(code)}";
    await mail.SendAsync(email, "Confirm your Media Insights email", $"Confirm your email: {link}");
    return Results.Ok(new { message = "Check your email to confirm your account." });
});
auth.MapGet("/confirm", async Task<IResult> (string userId, string code, UserManager<AppUser> users) =>
{
    var user = await users.FindByIdAsync(userId);
    if (user is null) return Results.BadRequest("Invalid link.");
    try
    {
        var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        var result = await users.ConfirmEmailAsync(user, token);
        return result.Succeeded ? (IResult)Results.Redirect("/?verified=1") : Results.BadRequest("Invalid link.");
    }
    catch (FormatException) { return Results.BadRequest("Invalid link."); }
});
auth.MapPost("/login", async Task<IResult> (LoginRequest request, SignInManager<AppUser> signIn, UserManager<AppUser> users) =>
{
    var user = await users.FindByEmailAsync(request.Email.Trim());
    if (user is null || user.Disabled) return Results.BadRequest(new { error = "Login failed." });
    var result = await signIn.PasswordSignInAsync(request.Email.Trim(), request.Password, false, lockoutOnFailure: true);
    return result.Succeeded ? (IResult)Results.Ok(new { message = "Signed in." }) :
        Results.BadRequest(new { error = result.IsNotAllowed ? "Confirm your email first." : "Login failed." });
});
auth.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
{ await signIn.SignOutAsync(); return Results.Ok(); }).RequireAuthorization();
app.MapGet("/api/auth/me", async Task<IResult> (System.Security.Claims.ClaimsPrincipal principal, UserManager<AppUser> users) =>
{
    var user = await users.GetUserAsync(principal);
    return user is null || user.Disabled ? (IResult)Results.Unauthorized() :
        Results.Ok(new { email = user.Email, admin = await users.IsInRoleAsync(user, "Admin") });
}).RequireAuthorization();
auth.MapPost("/change-password", async Task<IResult> (ChangePasswordRequest request, System.Security.Claims.ClaimsPrincipal principal,
    UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null) return Results.Unauthorized();
    var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
    if (!result.Succeeded) return Results.BadRequest(new { error = string.Join(" ", result.Errors.Select(e => e.Description)) });
    await signIn.RefreshSignInAsync(user);
    return Results.Ok();
}).RequireAuthorization();
auth.MapPost("/forgot-password", async (EmailRequest request, HttpContext http, UserManager<AppUser> users, IAppMailSender mail) =>
{
    var user = await users.FindByEmailAsync(request.Email.Trim());
    if (user is not null && await users.IsEmailConfirmedAsync(user))
    {
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
        var link = $"{http.Request.Scheme}://{http.Request.Host}/?reset=1&email={Uri.EscapeDataString(user.Email!)}&code={Uri.EscapeDataString(code)}";
        await mail.SendAsync(user.Email!, "Reset your Media Insights password", $"Reset your password: {link}");
    }
    return Results.Ok(new { message = "If the address exists, a reset email has been sent." });
});
auth.MapPost("/reset-password", async Task<IResult> (ResetRequest request, UserManager<AppUser> users) =>
{
    var user = await users.FindByEmailAsync(request.Email.Trim());
    if (user is null) return Results.BadRequest(new { error = "Invalid link." });
    try
    {
        var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Code));
        var result = await users.ResetPasswordAsync(user, token, request.NewPassword);
        return result.Succeeded ? (IResult)Results.Ok() :
            Results.BadRequest(new { error = string.Join(" ", result.Errors.Select(e => e.Description)) });
    }
    catch (FormatException) { return Results.BadRequest(new { error = "Invalid link." }); }
});
app.MapGet("/api/auth/admin/users", async (UserManager<AppUser> users) =>
{
    var entries = await users.Users.OrderBy(u => u.Email).Take(100).ToListAsync();
    var admins = (await users.GetUsersInRoleAsync("Admin")).Select(u => u.Id).ToHashSet();
    return Results.Ok(entries.Select(u => new { u.Id, u.Email, u.EmailConfirmed, u.Disabled, admin = admins.Contains(u.Id) }));
}).RequireAuthorization(p => p.RequireRole("Admin"));
app.MapPost("/api/auth/admin/users/{id}/disabled", async Task<IResult> (string id, DisableRequest request,
    System.Security.Claims.ClaimsPrincipal principal, UserManager<AppUser> users) =>
{
    var user = await users.FindByIdAsync(id);
    if (user is null) return Results.NotFound();
    if (user.Id == users.GetUserId(principal) || await users.IsInRoleAsync(user, "Admin"))
        return Results.BadRequest(new { error = "Admin accounts cannot be disabled here." });
    user.Disabled = request.Disabled;
    var result = await users.UpdateAsync(user);
    if (!result.Succeeded) return Results.BadRequest();
    await users.UpdateSecurityStampAsync(user);
    return Results.Ok();
}).RequireAuthorization(p => p.RequireRole("Admin"));
app.MapDelete("/api/auth/admin/users/{id}", async Task<IResult> (string id, UserManager<AppUser> users) =>
{
    var user = await users.FindByIdAsync(id);
    if (user is null) return Results.NotFound();
    if (await users.IsInRoleAsync(user, "Admin")) return Results.BadRequest(new { error = "Admin accounts cannot be deleted here." });
    var result = await users.DeleteAsync(user);
    return result.Succeeded ? Results.Ok() : Results.BadRequest(new { error = "Unable to delete account." });
}).RequireAuthorization(p => p.RequireRole("Admin"));
app.MapGet("/api/programmes", async (AppDbContext db) => Results.Ok(await db.Programmes.AsNoTracking().OrderBy(p => p.Id).ToListAsync()));
var mediaAdmin = app.MapGroup("/api/admin/programmes").RequireAuthorization(p => p.RequireRole("Admin"));
mediaAdmin.MapPost("", async Task<IResult> (MediaProgramme input, AppDbContext db) =>
{
    if (!input.IsValid()) return Results.BadRequest(new { error = "Provide names, categories, a valid medium and six non-negative daily values (maximum 100000000)." });
    input.Id = 0;
    db.Programmes.Add(input);
    await db.SaveChangesAsync();
    return Results.Ok(input);
});
mediaAdmin.MapPut("/{id:int}", async Task<IResult> (int id, MediaProgramme input, AppDbContext db) =>
{
    if (!input.IsValid()) return Results.BadRequest(new { error = "Invalid programme. Check all fields and six daily values." });
    var existing = await db.Programmes.FindAsync(id);
    if (existing is null) return Results.NotFound();
    input.Id = id;
    db.Entry(existing).CurrentValues.SetValues(input);
    await db.SaveChangesAsync();
    return Results.Ok(existing);
});
mediaAdmin.MapDelete("/{id:int}", async Task<IResult> (int id, AppDbContext db) =>
{
    var existing = await db.Programmes.FindAsync(id);
    if (existing is null) return Results.NotFound();
    db.Programmes.Remove(existing);
    await db.SaveChangesAsync();
    return Results.Ok();
});
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.Run();

record RegisterRequest(string Email, string Password);
record LoginRequest(string Email, string Password);
record EmailRequest(string Email);
record ChangePasswordRequest(string CurrentPassword, string NewPassword);
record ResetRequest(string Email, string Code, string NewPassword);
record DisableRequest(bool Disabled);

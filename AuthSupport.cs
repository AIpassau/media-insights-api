using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MediaInsights.Api;
public class AppUser : IdentityUser { public bool Disabled { get; set; } }
public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options) { public DbSet<MediaProgramme> Programmes => Set<MediaProgramme>();
    public DbSet<ContentInitialization> ContentInitialization => Set<ContentInitialization>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ContentInitialization>().Property(p => p.Id).ValueGeneratedNever();
    }
 }
public interface IAppMailSender { Task SendAsync(string address, string subject, string body); }
public class AppMailSender(IConfiguration config, IWebHostEnvironment environment, ILogger<AppMailSender> logger) : IAppMailSender
{
    public async Task SendAsync(string address, string subject, string body)
    {
        if (environment.IsDevelopment())
        {
            logger.LogWarning("DEVELOPMENT EMAIL to {Address}: {Subject} — {Body}", address, subject, body);
            return;
        }
        var host = config["Mail:Host"] ?? throw new InvalidOperationException("Mail:Host is required.");
        var from = config["Mail:From"] ?? throw new InvalidOperationException("Mail:From is required.");
        using var client = new SmtpClient(host, int.TryParse(config["Mail:Port"], out var port) ? port : 587)
        {
            EnableSsl = true, Credentials = new NetworkCredential(config["Mail:User"], config["Mail:Password"])
        };
        using var message = new MailMessage(from, address, subject, body);
        await client.SendMailAsync(message);
    }
}

public class MediaProgramme
{
    public int Id { get; set; }
    public string De { get; set; } = "";
    public string En { get; set; } = "";
    public string Medium { get; set; } = "TV";
    public string CategoryDe { get; set; } = "";
    public string CategoryEn { get; set; } = "";
    public int[] Daily { get; set; } = [];
    public bool IsValid() => new[] { De, En, CategoryDe, CategoryEn }.All(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 200)
        && new[] { "TV", "Radio", "Podcast" }.Contains(Medium)
        && Daily is { Length: 6 } && Daily.All(n => n >= 0 && n <= 100000000);
}

public class ContentInitialization { public int Id { get; set; } }

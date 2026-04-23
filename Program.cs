using Microsoft.EntityFrameworkCore;
using Tx9501.Data;
using Tx9501.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.SingleLine = true;
});
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

// Bind only to localhost for local development.
builder.WebHost.UseUrls(builder.Configuration["Server:Urls"] ?? "http://localhost:5000");

// Add MVC with views
builder.Services.AddControllersWithViews();

// IBM i service - reads data from IBM i DB2 via ODBC
builder.Services.AddScoped<IIBMiService, IBMiService>();

// SQLite local staging store for records created via web UI
builder.Services.AddDbContext<LocalDbContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("SQLite")
                   ?? "Data Source=tax_reporting_local.db")
        .EnableDetailedErrors()
        .EnableSensitiveDataLogging(
            builder.Environment.IsDevelopment() ||
            builder.Configuration.GetValue<bool>("Diagnostics:EnableEfSensitiveDataLogging")));

// Tax domain services
builder.Services.AddScoped<IYearSelectService, YearSelectService>();
builder.Services.AddScoped<IAssociationService, AssociationService>();
builder.Services.AddScoped<IClearTaxDataService, ClearTaxDataService>();
builder.Services.AddScoped<IBuildTaxDataService, BuildTaxDataService>();
builder.Services.AddScoped<IValidateTaxService, ValidateTaxService>();
builder.Services.AddScoped<ISummaryService, SummaryService>();
builder.Services.AddScoped<IMaintainService, MaintainService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IExtractService, ExtractService>();

// Session support (replaces IBM i interactive program state)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout        = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly    = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
});

// HttpContextAccessor for accessing user identity in services
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Ensure the local SQLite schema exists on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS [TaxAudits] (
            [Id] INTEGER NOT NULL CONSTRAINT [PK_TaxAudits] PRIMARY KEY AUTOINCREMENT,
            [TaxYear] TEXT NOT NULL,
            [Form] TEXT NOT NULL,
            [Asa] TEXT NOT NULL,
            [MbrNo] REAL NOT NULL,
            [MbrSub] TEXT NOT NULL,
            [SsiDn] REAL NOT NULL,
            [SsiDc] TEXT NOT NULL,
            [BorrName] TEXT NOT NULL,
            [BorrAddr] TEXT NOT NULL,
            [BorrAddrX] TEXT NOT NULL,
            [BorrCity] TEXT NOT NULL,
            [BorrState] TEXT NOT NULL,
            [BorrZip] REAL NOT NULL,
            [Errors] TEXT NOT NULL,
            [ReportToIrs] TEXT NOT NULL,
            [CorrIn] TEXT NOT NULL,
            [Foreign] TEXT NOT NULL,
            [ChangeDate] TEXT NOT NULL,
            [Dept] REAL NOT NULL,
            [IntPd] REAL NOT NULL,
            [Points] REAL NOT NULL,
            [InterN] REAL NOT NULL,
            [ErnWth] REAL NOT NULL,
            [DivRcv] REAL NOT NULL,
            [DivWth] REAL NOT NULL,
            [PatRef] REAL NOT NULL,
            [PatWth] REAL NOT NULL,
            [FmVal] REAL NOT NULL,
            [UnpPrn] REAL NOT NULL,
            [DteAqr] TEXT NOT NULL,
            [PrDesc] TEXT NOT NULL,
            [Compen] REAL NOT NULL,
            [Rents] REAL NOT NULL,
            [MedPay] REAL NOT NULL,
            [LglPay] REAL NOT NULL,
            [Other] REAL NOT NULL,
            [WthHeld] REAL NOT NULL,
            [OrigDate] TEXT NOT NULL,
            [SecSame] TEXT NOT NULL,
            [SecAddr] TEXT NOT NULL,
            [SecDesc] TEXT NOT NULL,
            [SecOther] TEXT NOT NULL,
            [SecNum] REAL NOT NULL,
            [MtgAcqDt] TEXT NOT NULL,
            [AuditCreatedAt] TEXT NOT NULL
        );");
}

if (app.Environment.IsDevelopment())
{
    if (app.Configuration.GetValue<bool>("Server:UseHttpsRedirection"))
    {
        app.UseHttpsRedirection();
    }
}
else
{
    app.UseExceptionHandler("/TaxReporting/Error");
    app.UseHsts();
}
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=TaxReporting}/{action=YearSelect}/{id?}");

app.Run();

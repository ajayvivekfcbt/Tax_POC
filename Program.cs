using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;
using Tx9501.Data;
using Tx9501.Models;
using Tx9501.Services;

var builder = WebApplication.CreateBuilder(args);

// Build a full IBM i ODBC string once so all services share the same credentials source.
var ibmiBaseCs = builder.Configuration.GetConnectionString("IBMi");
var ibmiUser = builder.Configuration["IBMiCredentials:Username"] ?? string.Empty;
var ibmiPass = builder.Configuration["IBMiCredentials:Password"] ?? string.Empty;
if (!string.IsNullOrWhiteSpace(ibmiBaseCs))
{
    var hasUid = ibmiBaseCs.Contains("UID=", StringComparison.OrdinalIgnoreCase);
    var hasPwd = ibmiBaseCs.Contains("PWD=", StringComparison.OrdinalIgnoreCase);
    if (!hasUid || !hasPwd)
    {
        builder.Configuration["ConnectionStrings:IBMi"] =
            $"{ibmiBaseCs.TrimEnd(';')};UID={ibmiUser};PWD={ibmiPass}";
    }
}

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    options.SingleLine = true;
});
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

// Bind only to localhost for local development.
builder.WebHost.UseUrls(builder.Configuration["Server:Urls"] ?? "http://localhost:5001");

// Add MVC with views.
builder.Services.AddControllersWithViews();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

builder.Services.Configure<LdapOptions>(builder.Configuration.GetSection("Ldap"));

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

builder.Services.AddDbContextFactory<LocalDbContext>(opts =>
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
builder.Services.AddScoped<TaxDetailSourceService>();
builder.Services.AddScoped<TaxDetailTransformService>();
builder.Services.AddScoped<TX9515BuildService>();
builder.Services.AddScoped<TX9540MiscNecService>();
builder.Services.AddScoped<TX9517PatronageService>();
builder.Services.AddScoped<TX9526ValidationService>();
builder.Services.AddScoped<IValidateTaxService, ValidateTaxService>();
builder.Services.AddScoped<ISummaryService, SummaryService>();
builder.Services.AddScoped<IMaintainService, MaintainService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IExtractService, ExtractService>();
builder.Services.AddScoped<ILdapAuthenticationService, LdapAuthenticationService>();

// Validation state service - thread-safe state management for background validation tasks
builder.Services.AddSingleton<IValidationStateService, ValidationStateService>();
builder.Services.AddSingleton<IBuildStateService, BuildStateService>();

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

    static bool TableExists(DbConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$name";
        p.Value = tableName;
        cmd.Parameters.Add(p);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    static string? GetDeclaredColumnType(DbConnection connection, string tableName, string columnName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info([{tableName}]);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader[1]?.ToString();
            if (!string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return reader[2]?.ToString();
        }

        return null;
    }

    static bool ColumnDeclaredInteger(DbConnection connection, string tableName, string columnName)
    {
        var declared = GetDeclaredColumnType(connection, tableName, columnName);
        return string.Equals(declared, "INTEGER", StringComparison.OrdinalIgnoreCase);
    }

    static void RebuildTaxDetailsAsInteger(LocalDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"
            DROP TABLE IF EXISTS [TaxDetails_tmp];

            CREATE TABLE [TaxDetails_tmp] (
                [Id] INTEGER NOT NULL CONSTRAINT [PK_TaxDetails] PRIMARY KEY AUTOINCREMENT,
                [TaxYear] TEXT NOT NULL,
                [Form] TEXT NOT NULL,
                [Asa] TEXT NOT NULL,
                [MbrNo] REAL NOT NULL,
                [MbrSub] TEXT NOT NULL,
                [SsiDn] INTEGER NOT NULL,
                [SsiDc] TEXT NOT NULL,
                [BorrName] TEXT NOT NULL,
                [BorrAddr] TEXT NOT NULL,
                [BorrAddrX] TEXT NOT NULL,
                [BorrCity] TEXT NOT NULL,
                [BorrState] TEXT NOT NULL,
                [BorrZip] INTEGER NOT NULL,
                [Errors] TEXT NOT NULL,
                [ReportToIrs] TEXT NOT NULL,
                [NonRptReason] TEXT NOT NULL,
                [CorrIn] TEXT NOT NULL,
                [Foreign] TEXT NOT NULL,
                [ChangeDate] TEXT NOT NULL,
                [Dept] INTEGER NOT NULL,
                [IntPd] INTEGER NOT NULL,
                [Points] INTEGER NOT NULL,
                [InterN] INTEGER NOT NULL,
                [ErnWth] INTEGER NOT NULL,
                [DivRcv] INTEGER NOT NULL,
                [DivWth] INTEGER NOT NULL,
                [PatRef] INTEGER NOT NULL,
                [PatWth] INTEGER NOT NULL,
                [FmVal] INTEGER NOT NULL,
                [UnpPrn] INTEGER NOT NULL,
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
                [SecNum] INTEGER NOT NULL,
                [MtgAcqDt] TEXT NOT NULL,
                [CreatedAt] TEXT NOT NULL,
                [UpdatedAt] TEXT NOT NULL
            );

            INSERT INTO [TaxDetails_tmp] (
                [Id],[TaxYear],[Form],[Asa],[MbrNo],[MbrSub],[SsiDn],[SsiDc],[BorrName],[BorrAddr],[BorrAddrX],[BorrCity],[BorrState],[BorrZip],
                [Errors],[ReportToIrs],[NonRptReason],[CorrIn],[Foreign],[ChangeDate],[Dept],[IntPd],[Points],[InterN],[ErnWth],[DivRcv],[DivWth],
                [PatRef],[PatWth],[FmVal],[UnpPrn],[DteAqr],[PrDesc],[Compen],[Rents],[MedPay],[LglPay],[Other],[WthHeld],[OrigDate],[SecSame],
                [SecAddr],[SecDesc],[SecOther],[SecNum],[MtgAcqDt],[CreatedAt],[UpdatedAt])
            SELECT
                [Id],
                IFNULL([TaxYear], ''),
                IFNULL([Form], ''),
                IFNULL([Asa], ''),
                IFNULL([MbrNo], 0),
                IFNULL([MbrSub], ''),
                CAST(IFNULL([SsiDn], 0) AS INTEGER),
                IFNULL([SsiDc], ''),
                IFNULL([BorrName], ''),
                IFNULL([BorrAddr], ''),
                IFNULL([BorrAddrX], ''),
                IFNULL([BorrCity], ''),
                IFNULL([BorrState], ''),
                CAST(IFNULL([BorrZip], 0) AS INTEGER),
                IFNULL([Errors], ''),
                IFNULL([ReportToIrs], ''),
                IFNULL([NonRptReason], ''),
                IFNULL([CorrIn], ''),
                IFNULL([Foreign], ''),
                IFNULL([ChangeDate], ''),
                CAST(IFNULL([Dept], 0) AS INTEGER),
                CAST(IFNULL([IntPd], 0) AS INTEGER),
                CAST(IFNULL([Points], 0) AS INTEGER),
                CAST(IFNULL([InterN], 0) AS INTEGER),
                CAST(IFNULL([ErnWth], 0) AS INTEGER),
                CAST(IFNULL([DivRcv], 0) AS INTEGER),
                CAST(IFNULL([DivWth], 0) AS INTEGER),
                CAST(IFNULL([PatRef], 0) AS INTEGER),
                CAST(IFNULL([PatWth], 0) AS INTEGER),
                CAST(IFNULL([FmVal], 0) AS INTEGER),
                CAST(IFNULL([UnpPrn], 0) AS INTEGER),
                IFNULL([DteAqr], ''),
                IFNULL([PrDesc], ''),
                IFNULL([Compen], 0),
                IFNULL([Rents], 0),
                IFNULL([MedPay], 0),
                IFNULL([LglPay], 0),
                IFNULL([Other], 0),
                IFNULL([WthHeld], 0),
                IFNULL([OrigDate], ''),
                IFNULL([SecSame], ''),
                IFNULL([SecAddr], ''),
                IFNULL([SecDesc], ''),
                IFNULL([SecOther], ''),
                CAST(IFNULL([SecNum], 0) AS INTEGER),
                IFNULL([MtgAcqDt], ''),
                IFNULL([CreatedAt], '0001-01-01 00:00:00'),
                IFNULL([UpdatedAt], '0001-01-01 00:00:00')
            FROM [TaxDetails];

            DROP TABLE [TaxDetails];
            ALTER TABLE [TaxDetails_tmp] RENAME TO [TaxDetails];
        ");
    }

    static void RebuildTaxAuditsAsInteger(LocalDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"
            DROP TABLE IF EXISTS [TaxAudits_tmp];

            CREATE TABLE [TaxAudits_tmp] (
                [Id] INTEGER NOT NULL CONSTRAINT [PK_TaxAudits] PRIMARY KEY AUTOINCREMENT,
                [TaxYear] TEXT NOT NULL,
                [Form] TEXT NOT NULL,
                [Asa] TEXT NOT NULL,
                [MbrNo] REAL NOT NULL,
                [MbrSub] TEXT NOT NULL,
                [SsiDn] INTEGER NOT NULL,
                [SsiDc] TEXT NOT NULL,
                [BorrName] TEXT NOT NULL,
                [BorrAddr] TEXT NOT NULL,
                [BorrAddrX] TEXT NOT NULL,
                [BorrCity] TEXT NOT NULL,
                [BorrState] TEXT NOT NULL,
                [BorrZip] INTEGER NOT NULL,
                [Errors] TEXT NOT NULL,
                [ReportToIrs] TEXT NOT NULL,
                [CorrIn] TEXT NOT NULL,
                [Foreign] TEXT NOT NULL,
                [ChangeDate] TEXT NOT NULL,
                [Dept] INTEGER NOT NULL,
                [IntPd] INTEGER NOT NULL,
                [Points] INTEGER NOT NULL,
                [InterN] INTEGER NOT NULL,
                [ErnWth] INTEGER NOT NULL,
                [DivRcv] INTEGER NOT NULL,
                [DivWth] INTEGER NOT NULL,
                [PatRef] INTEGER NOT NULL,
                [PatWth] INTEGER NOT NULL,
                [FmVal] INTEGER NOT NULL,
                [UnpPrn] INTEGER NOT NULL,
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
                [SecNum] INTEGER NOT NULL,
                [MtgAcqDt] TEXT NOT NULL,
                [AuditCreatedAt] TEXT NOT NULL
            );

            INSERT INTO [TaxAudits_tmp] (
                [Id],[TaxYear],[Form],[Asa],[MbrNo],[MbrSub],[SsiDn],[SsiDc],[BorrName],[BorrAddr],[BorrAddrX],[BorrCity],[BorrState],[BorrZip],
                [Errors],[ReportToIrs],[CorrIn],[Foreign],[ChangeDate],[Dept],[IntPd],[Points],[InterN],[ErnWth],[DivRcv],[DivWth],[PatRef],[PatWth],
                [FmVal],[UnpPrn],[DteAqr],[PrDesc],[Compen],[Rents],[MedPay],[LglPay],[Other],[WthHeld],[OrigDate],[SecSame],[SecAddr],[SecDesc],
                [SecOther],[SecNum],[MtgAcqDt],[AuditCreatedAt])
            SELECT
                [Id],
                IFNULL([TaxYear], ''),
                IFNULL([Form], ''),
                IFNULL([Asa], ''),
                IFNULL([MbrNo], 0),
                IFNULL([MbrSub], ''),
                CAST(IFNULL([SsiDn], 0) AS INTEGER),
                IFNULL([SsiDc], ''),
                IFNULL([BorrName], ''),
                IFNULL([BorrAddr], ''),
                IFNULL([BorrAddrX], ''),
                IFNULL([BorrCity], ''),
                IFNULL([BorrState], ''),
                CAST(IFNULL([BorrZip], 0) AS INTEGER),
                IFNULL([Errors], ''),
                IFNULL([ReportToIrs], ''),
                IFNULL([CorrIn], ''),
                IFNULL([Foreign], ''),
                IFNULL([ChangeDate], ''),
                CAST(IFNULL([Dept], 0) AS INTEGER),
                CAST(IFNULL([IntPd], 0) AS INTEGER),
                CAST(IFNULL([Points], 0) AS INTEGER),
                CAST(IFNULL([InterN], 0) AS INTEGER),
                CAST(IFNULL([ErnWth], 0) AS INTEGER),
                CAST(IFNULL([DivRcv], 0) AS INTEGER),
                CAST(IFNULL([DivWth], 0) AS INTEGER),
                CAST(IFNULL([PatRef], 0) AS INTEGER),
                CAST(IFNULL([PatWth], 0) AS INTEGER),
                CAST(IFNULL([FmVal], 0) AS INTEGER),
                CAST(IFNULL([UnpPrn], 0) AS INTEGER),
                IFNULL([DteAqr], ''),
                IFNULL([PrDesc], ''),
                IFNULL([Compen], 0),
                IFNULL([Rents], 0),
                IFNULL([MedPay], 0),
                IFNULL([LglPay], 0),
                IFNULL([Other], 0),
                IFNULL([WthHeld], 0),
                IFNULL([OrigDate], ''),
                IFNULL([SecSame], ''),
                IFNULL([SecAddr], ''),
                IFNULL([SecDesc], ''),
                IFNULL([SecOther], ''),
                CAST(IFNULL([SecNum], 0) AS INTEGER),
                IFNULL([MtgAcqDt], ''),
                IFNULL([AuditCreatedAt], '0001-01-01 00:00:00')
            FROM [TaxAudits];

            DROP TABLE [TaxAudits];
            ALTER TABLE [TaxAudits_tmp] RENAME TO [TaxAudits];
        ");
    }

    var conn = db.Database.GetDbConnection();
    var shouldCloseConnection = conn.State != ConnectionState.Open;
    if (shouldCloseConnection)
    {
        conn.Open();
    }

    try
    {
        var integerColumns = new[]
        {
            "SsiDn", "BorrZip", "Dept", "IntPd", "Points", "InterN", "ErnWth",
            "DivRcv", "DivWth", "PatRef", "PatWth", "UnpPrn", "FmVal", "SecNum"
        };

        if (TableExists(conn, "TaxDetails") && integerColumns.Any(col => !ColumnDeclaredInteger(conn, "TaxDetails", col)))
        {
            RebuildTaxDetailsAsInteger(db);
        }

        if (TableExists(conn, "TaxAudits") && integerColumns.Any(col => !ColumnDeclaredInteger(conn, "TaxAudits", col)))
        {
            RebuildTaxAuditsAsInteger(db);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info([TaxDetails]);";
        using var reader = cmd.ExecuteReader();
        var hasNonRptReasonColumn = false;
        while (reader.Read())
        {
            if (string.Equals(reader[1]?.ToString(), "NonRptReason", StringComparison.OrdinalIgnoreCase))
            {
                hasNonRptReasonColumn = true;
                break;
            }
        }

        if (!hasNonRptReasonColumn)
        {
            db.Database.ExecuteSqlRaw(@"
                ALTER TABLE [TaxDetails]
                ADD COLUMN [NonRptReason] TEXT NOT NULL DEFAULT '';
            ");
        }

        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS [IX_TaxDetails_TaxYear_Form_Asa]
            ON [TaxDetails] ([TaxYear], [Form], [Asa]);
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS [IX_TaxDetails_TaxYear_Form_Asa_MbrNo_MbrSub]
            ON [TaxDetails] ([TaxYear], [Form], [Asa], [MbrNo], [MbrSub]);
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS [IX_TaxAudits_TaxYear_Form_Asa_MbrNo_MbrSub]
            ON [TaxAudits] ([TaxYear], [Form], [Asa], [MbrNo], [MbrSub]);
        ");

        // Ensure identity fields remain whole numbers in existing SQLite databases.
        db.Database.ExecuteSqlRaw(@"
            UPDATE [TaxDetails]
            SET [SsiDn] = CAST([SsiDn] AS INTEGER),
                [BorrZip] = CAST([BorrZip] AS INTEGER),
                [Dept] = CAST([Dept] AS INTEGER),
                [IntPd] = CAST([IntPd] AS INTEGER),
                [Points] = CAST([Points] AS INTEGER),
                [InterN] = CAST([InterN] AS INTEGER),
                [ErnWth] = CAST([ErnWth] AS INTEGER),
                [DivRcv] = CAST([DivRcv] AS INTEGER),
                [DivWth] = CAST([DivWth] AS INTEGER),
                [PatRef] = CAST([PatRef] AS INTEGER),
                [PatWth] = CAST([PatWth] AS INTEGER),
                [UnpPrn] = CAST([UnpPrn] AS INTEGER),
                [FmVal] = CAST([FmVal] AS INTEGER),
                [SecNum] = CAST([SecNum] AS INTEGER)
            WHERE [SsiDn] <> CAST([SsiDn] AS INTEGER)
               OR [BorrZip] <> CAST([BorrZip] AS INTEGER)
               OR [Dept] <> CAST([Dept] AS INTEGER)
               OR [IntPd] <> CAST([IntPd] AS INTEGER)
               OR [Points] <> CAST([Points] AS INTEGER)
               OR [InterN] <> CAST([InterN] AS INTEGER)
               OR [ErnWth] <> CAST([ErnWth] AS INTEGER)
               OR [DivRcv] <> CAST([DivRcv] AS INTEGER)
               OR [DivWth] <> CAST([DivWth] AS INTEGER)
               OR [PatRef] <> CAST([PatRef] AS INTEGER)
               OR [PatWth] <> CAST([PatWth] AS INTEGER)
               OR [UnpPrn] <> CAST([UnpPrn] AS INTEGER)
               OR [FmVal] <> CAST([FmVal] AS INTEGER)
               OR [SecNum] <> CAST([SecNum] AS INTEGER);
        ");

        db.Database.ExecuteSqlRaw(@"
            UPDATE [TaxAudits]
            SET [SsiDn] = CAST([SsiDn] AS INTEGER),
                [BorrZip] = CAST([BorrZip] AS INTEGER),
                [Dept] = CAST([Dept] AS INTEGER),
                [IntPd] = CAST([IntPd] AS INTEGER),
                [Points] = CAST([Points] AS INTEGER),
                [InterN] = CAST([InterN] AS INTEGER),
                [ErnWth] = CAST([ErnWth] AS INTEGER),
                [DivRcv] = CAST([DivRcv] AS INTEGER),
                [DivWth] = CAST([DivWth] AS INTEGER),
                [PatRef] = CAST([PatRef] AS INTEGER),
                [PatWth] = CAST([PatWth] AS INTEGER),
                [UnpPrn] = CAST([UnpPrn] AS INTEGER),
                [FmVal] = CAST([FmVal] AS INTEGER),
                [SecNum] = CAST([SecNum] AS INTEGER)
            WHERE [SsiDn] <> CAST([SsiDn] AS INTEGER)
               OR [BorrZip] <> CAST([BorrZip] AS INTEGER)
               OR [Dept] <> CAST([Dept] AS INTEGER)
               OR [IntPd] <> CAST([IntPd] AS INTEGER)
               OR [Points] <> CAST([Points] AS INTEGER)
               OR [InterN] <> CAST([InterN] AS INTEGER)
               OR [ErnWth] <> CAST([ErnWth] AS INTEGER)
               OR [DivRcv] <> CAST([DivRcv] AS INTEGER)
               OR [DivWth] <> CAST([DivWth] AS INTEGER)
               OR [PatRef] <> CAST([PatRef] AS INTEGER)
               OR [PatWth] <> CAST([PatWth] AS INTEGER)
               OR [UnpPrn] <> CAST([UnpPrn] AS INTEGER)
               OR [FmVal] <> CAST([FmVal] AS INTEGER)
               OR [SecNum] <> CAST([SecNum] AS INTEGER);
        ");
    }
    finally
    {
        if (shouldCloseConnection)
        {
            conn.Close();
        }
    }

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS [TaxAudits] (
            [Id] INTEGER NOT NULL CONSTRAINT [PK_TaxAudits] PRIMARY KEY AUTOINCREMENT,
            [TaxYear] TEXT NOT NULL,
            [Form] TEXT NOT NULL,
            [Asa] TEXT NOT NULL,
            [MbrNo] REAL NOT NULL,
            [MbrSub] TEXT NOT NULL,
            [SsiDn] INTEGER NOT NULL,
            [SsiDc] TEXT NOT NULL,
            [BorrName] TEXT NOT NULL,
            [BorrAddr] TEXT NOT NULL,
            [BorrAddrX] TEXT NOT NULL,
            [BorrCity] TEXT NOT NULL,
            [BorrState] TEXT NOT NULL,
            [BorrZip] INTEGER NOT NULL,
            [Errors] TEXT NOT NULL,
            [ReportToIrs] TEXT NOT NULL,
            [CorrIn] TEXT NOT NULL,
            [Foreign] TEXT NOT NULL,
            [ChangeDate] TEXT NOT NULL,
            [Dept] INTEGER NOT NULL,
            [IntPd] INTEGER NOT NULL,
            [Points] INTEGER NOT NULL,
            [InterN] INTEGER NOT NULL,
            [ErnWth] INTEGER NOT NULL,
            [DivRcv] INTEGER NOT NULL,
            [DivWth] INTEGER NOT NULL,
            [PatRef] INTEGER NOT NULL,
            [PatWth] INTEGER NOT NULL,
            [FmVal] INTEGER NOT NULL,
            [UnpPrn] INTEGER NOT NULL,
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
            [SecNum] INTEGER NOT NULL,
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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "admin-root",
    pattern: "Admin",
    defaults: new { controller = "Admin", action = "Index" });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=TaxReporting}/{action=YearSelect}/{id?}");

app.Run();

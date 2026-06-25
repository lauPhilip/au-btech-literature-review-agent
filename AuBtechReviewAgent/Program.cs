using AuBtechReviewAgent.Components;

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register the UI memory cache container as Scoped (per user connection session)
builder.Services.AddScoped<AuBtechReviewAgent.UiStateContainer>();

// .NET automatically checks both appsettings.json and your User Secrets vault for these keys
string mistralApiKey = builder.Configuration["MISTRAL_API_KEY"] ?? throw new InvalidOperationException("Mistral Key missing.");
string elsevierApiKey = builder.Configuration["ELSEVIER_API_KEY"] ?? "";
string ieeeApiKey = builder.Configuration["IEEE_API_KEY"] ?? "";
string scholarApiKey = builder.Configuration["SCHOLAR_API_KEY"] ?? ""; 

builder.Services.AddSingleton(new AuBtechReviewAgent.PrismaReviewEngine(mistralApiKey, elsevierApiKey, ieeeApiKey, scholarApiKey));

var app = builder.Build(); 

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
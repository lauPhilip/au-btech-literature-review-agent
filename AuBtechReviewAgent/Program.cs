using AuBtechReviewAgent.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

    // Register the UI memory cache container as Scoped (per user connection session)
builder.Services.AddScoped<AuBtechReviewAgent.UiStateContainer>();

// ─── PLUG YOUR SERVICE REGISTRATION HERE ──────────────────────────────────
string mistralApiKey = builder.Configuration["MISTRAL_API_KEY"] ?? throw new InvalidOperationException("Mistral API Key is missing from configuration setup.");
builder.Services.AddSingleton(new AuBtechReviewAgent.PrismaReviewEngine(mistralApiKey));
// ──────────────────────────────────────────────────────────────────────────

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
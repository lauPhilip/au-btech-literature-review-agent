using AuBtechReviewAgent.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

    // Register the UI memory cache container as Scoped (per user connection session)
builder.Services.AddScoped<AuBtechReviewAgent.UiStateContainer>();

string mistralApiKey = builder.Configuration["MISTRAL_API_KEY"] ?? throw new InvalidOperationException("Mistral Key missing.");
string elsevierApiKey = builder.Configuration["ELSEVIER_API_KEY"] ?? "";

// Simplified dependency injection registration
builder.Services.AddSingleton(new AuBtechReviewAgent.PrismaReviewEngine(mistralApiKey, elsevierApiKey));

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
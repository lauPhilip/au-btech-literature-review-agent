# Web

Helpers for the website around the review. `SiteSeo.cs` serves robots.txt, the sitemap and structured data; `SiteHealth.cs` answers `/health`. `SecurityHeaders.cs` sets the Content-Security-Policy and the other security headers, and `SecurityUtility.cs` cleans API keys and other input before they are used. `RunQuotaService.cs` counts free runs per network address. `Glossary.cs` holds the plain-language explanations shown as help, `UiStateContainer.cs` is the dashboard's state for one browser session, and `UserApiKeys.cs` is the keys a user entered.

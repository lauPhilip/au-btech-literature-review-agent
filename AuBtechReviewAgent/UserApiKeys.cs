namespace AuBtechReviewAgent;

public record UserApiKeys(
    string? MistralApiKey = null,
    string? ElsevierApiKey = null,
    string? IeeeApiKey = null,
    string? ScholarApiKey = null
);
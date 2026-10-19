using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace IisDeployApi;

internal static class SwaggerUiTheme
{
    public static void Apply(SwaggerUIOptions swagger, IConfiguration configuration, string environmentName)
    {
        var label = configuration["SwaggerUi:EnvironmentName"]?.Trim();
        if (string.IsNullOrWhiteSpace(label)) label = environmentName;

        var color = configuration["SwaggerUi:AccentColor"]?.Trim();
        if (string.IsNullOrWhiteSpace(color))
        {
            color = environmentName.ToUpperInvariant() switch
            {
                "DEVELOPMENT" => "#2563EB",
                "STAGING" => "#B45309",
                "PRODUCTION" => "#B91C1C",
                _ => "#475569"
            };
        }
        if (color.Length != 7 || color[0] != '#' || color.Skip(1).Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("SwaggerUi:AccentColor must be a six-digit hex color such as #B91C1C, or empty for an automatic color.");

        var foreground = TextColor(color);
        // Percent signs must not become Swashbuckle template placeholders in the banner.
        var encodedLabel = HtmlEncoder.Default.Encode(label).Replace("%", "&#37;", StringComparison.Ordinal);
        swagger.DocumentTitle = $"IIS Deployment API - {label}";
        swagger.HeadContent += $$"""
            <style id="swagger-environment-theme">
                :root {
                    --environment-accent: {{color}};
                    --environment-foreground: {{foreground}};
                }
                #swagger-environment {
                    position: sticky;
                    top: 0;
                    z-index: 10000;
                    padding: 12px 20px;
                    background: var(--environment-accent);
                    color: var(--environment-foreground);
                    font: 600 15px/1.4 system-ui, sans-serif;
                    text-align: center;
                    overflow-wrap: anywhere;
                    box-shadow: 0 2px 6px #0003;
                }
                #swagger-environment strong { font-weight: 800; }
                .swagger-ui .topbar { background-color: var(--environment-accent); }
                .swagger-ui .info .title {
                    border-left: 6px solid var(--environment-accent);
                    padding-left: 12px;
                }
                .swagger-ui .scheme-container { border-top: 3px solid var(--environment-accent); }
            </style>
            """;

        // Reuse Swashbuckle's template and its relative asset URLs, including under /cicd.
        var defaultIndexStream = swagger.IndexStream;
        swagger.IndexStream = () =>
        {
            using var reader = new StreamReader(defaultIndexStream());
            var html = reader.ReadToEnd();
            const string bodyMarker = "<body>";
            if (!html.Contains(bodyMarker, StringComparison.Ordinal))
                throw new InvalidOperationException("The Swagger UI template has no body element for its environment banner.");
            var banner = $"<div id=\"swagger-environment\" role=\"note\" aria-label=\"Environment\">Environment: <strong>{encodedLabel}</strong></div>";
            return new MemoryStream(Encoding.UTF8.GetBytes(
                html.Replace(bodyMarker, bodyMarker + banner, StringComparison.Ordinal)));
        };
    }

    private static string TextColor(string color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var red = byte.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var green = byte.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var blue = byte.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var luminance = 0.2126 * Linear(red) + 0.7152 * Linear(green) + 0.0722 * Linear(blue);
        return 1.05 / (luminance + 0.05) >= (luminance + 0.05) / 0.05 ? "#FFFFFF" : "#000000";
    }
}

using System.Text.Json;
using AutoReparos.Execucao.Application;
using AutoReparos.Execucao.Domain;

namespace AutoReparos.Execucao.Api;

internal static class CorpoJson
{
    public static async Task<JsonElement?> LerObjetoAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType()) return null;
        try
        {
            using var documento = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            return documento.RootElement.ValueKind == JsonValueKind.Object ? documento.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool TentarLerTipoEtapa(JsonElement corpo, out TipoEtapa tipo)
    {
        tipo = default;
        if (!LeitorJson.TentarLerTexto(corpo, "tipo", 40, out var texto)) return false;
        switch (texto!.Trim().ToLowerInvariant())
        {
            case "diagnostico" or "diagnóstico": tipo = TipoEtapa.Diagnostico; return true;
            case "reparo": tipo = TipoEtapa.Reparo; return true;
            default: return false;
        }
    }
}

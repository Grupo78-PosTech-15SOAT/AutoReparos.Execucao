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

    // Nome, descrição opcional (null ou ausente vale nulo) e preço unitário decimal; a regra de faixa é do domínio.
    public static bool TentarLerCatalogo(JsonElement corpo, out string? nome, out string? descricao, out decimal preco)
    {
        descricao = null; preco = 0;
        if (!LeitorJson.TentarLerTexto(corpo, "nome", LimitesCatalogo.NomeMaximo, out nome)) return false;
        if (LeitorJson.Propriedade(corpo, "descricao", out var d) && d.ValueKind != JsonValueKind.Null
            && (!LeitorJson.TentarLerString(d, out descricao) || string.IsNullOrWhiteSpace(descricao) || descricao!.Length > LimitesCatalogo.DescricaoMaxima || descricao.Contains((char)0))) return false;
        return LeitorJson.Propriedade(corpo, "precoUnitario", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out preco);
    }

    public static bool TentarLerInteiro(JsonElement corpo, string nome, out int valor)
    {
        valor = 0;
        return LeitorJson.Propriedade(corpo, nome, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out valor);
    }

    // Campo opcional: ausente ou null vale o padrão; presente precisa ser inteiro entre o padrão (mínimo) e o saldo máximo.
    public static bool TentarLerInteiroOpcional(JsonElement corpo, string nome, int padrao, out int valor)
    {
        valor = padrao;
        if (!LeitorJson.Propriedade(corpo, nome, out var e) || e.ValueKind == JsonValueKind.Null) return true;
        return TentarLerInteiro(corpo, nome, out valor) && valor >= padrao && valor <= LimitesCatalogo.QuantidadeMaxima;
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

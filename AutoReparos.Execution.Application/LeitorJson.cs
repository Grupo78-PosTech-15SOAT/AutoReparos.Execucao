using System.Text.Json;

namespace AutoReparos.Execution.Application;

// Leitura estrita de JSON: nenhum acesso lança exceção, tudo devolve false para entrada inválida.
public static class LeitorJson
{
    public static bool TentarLerObjeto(string? json, out JsonElement objeto)
    {
        objeto = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var documento = JsonDocument.Parse(json);
            if (documento.RootElement.ValueKind != JsonValueKind.Object) return false;
            objeto = documento.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static bool Propriedade(JsonElement corpo, string nome, out JsonElement elemento)
    {
        foreach (var propriedade in corpo.EnumerateObject())
        {
            if (!string.Equals(propriedade.Name, nome, StringComparison.OrdinalIgnoreCase)) continue;
            elemento = propriedade.Value;
            return true;
        }
        elemento = default;
        return false;
    }

    // GetString lança InvalidOperationException para surrogate isolado em strings JSON.
    public static bool TentarLerString(JsonElement elemento, out string? texto)
    {
        texto = null;
        if (elemento.ValueKind != JsonValueKind.String) return false;
        try { texto = elemento.GetString(); return texto is not null; }
        catch (InvalidOperationException) { return false; }
    }

    // Texto obrigatório: não vazio, até o limite, sem NUL (o PostgreSQL rejeita 22021).
    public static bool TentarLerTexto(JsonElement corpo, string nome, int maximo, out string? valor)
    {
        valor = null;
        if (!Propriedade(corpo, nome, out var elemento) || !TentarLerString(elemento, out var texto)) return false;
        if (string.IsNullOrWhiteSpace(texto) || texto.Length > maximo || texto.Contains((char)0)) return false;
        valor = texto;
        return true;
    }

    // Aceita somente o formato canônico 8-4-4-4-12 (sem chaves, sem espaços, sem forma compacta).
    public static bool TentarLerGuid(JsonElement corpo, string nome, out Guid valor)
    {
        valor = Guid.Empty;
        return Propriedade(corpo, nome, out var elemento) && TentarLerString(elemento, out var texto)
            && texto!.Length == 36 && Guid.TryParseExact(texto, "D", out valor);
    }
}

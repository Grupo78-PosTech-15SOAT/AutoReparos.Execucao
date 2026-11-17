using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoReparos.Execucao.Application;

// Propagação W3C do trace dentro do envelope JSON das mensagens (campos opcionais `traceparent` e `tracestate`, D-65).
public static class ContextoTrace
{
    public const string NomeFonte = "AutoReparos.Execucao.Mensageria";
    public static readonly ActivitySource Fonte = new(NomeFonte);

    // Grava o contexto da Activity corrente na raiz do envelope; sem Activity W3C corrente, não grava nada.
    public static void Injetar(JsonObject envelope)
    {
        var atual = Activity.Current;
        if (atual is null || atual.IdFormat != ActivityIdFormat.W3C) return;
        envelope["traceparent"] = atual.Id;
        if (!string.IsNullOrEmpty(atual.TraceStateString)) envelope["tracestate"] = atual.TraceStateString;
    }

    // Serializa o envelope já com o contexto da Activity corrente. O JSON do envelope sai igual ao de
    // JsonSerializer.Serialize (sem passar por JsonNode, que reescaparia datas como `+00:00`);
    // os campos de trace são acrescentados no fim do objeto.
    public static string Serializar(object envelope)
    {
        var json = JsonSerializer.Serialize(envelope);
        var atual = Activity.Current;
        if (atual is null || atual.IdFormat != ActivityIdFormat.W3C || json.Length < 2 || json[^1] != '}') return json;
        var campos = $"\"traceparent\":{JsonSerializer.Serialize(atual.Id)}";
        if (!string.IsNullOrEmpty(atual.TraceStateString))
            campos += $",\"tracestate\":{JsonSerializer.Serialize(atual.TraceStateString)}";
        var vazio = json.Length == 2;
        return string.Concat(json.AsSpan(0, json.Length - 1), vazio ? "" : ",", campos, "}");
    }

    // Contexto remoto do envelope; ausente ou inválido devolve nulo (a mensagem segue o processamento normal).
    public static ActivityContext? Extrair(JsonElement raiz)
    {
        if (raiz.ValueKind != JsonValueKind.Object || !TentarLerTexto(raiz, "traceparent", out var traceparent)) return null;
        TentarLerTexto(raiz, "tracestate", out var tracestate);
        return ActivityContext.TryParse(traceparent, tracestate, isRemote: true, out var contexto) ? contexto : null;
    }

    public static ActivityContext? Extrair(string? json) =>
        LeitorJson.TentarLerObjeto(json, out var raiz) ? Extrair(raiz) : null;

    // Inicia a Activity de mensageria com o pai do envelope; sem pai, é raiz (a Activity ambiente do laço não é herdada).
    // Chamar de dentro de um método async: a troca de Activity.Current vale só até ele terminar.
    public static Activity? Iniciar(string nome, ActivityKind tipo, ActivityContext? pai)
    {
        Activity.Current = null;
        return Fonte.StartActivity(nome, tipo, pai ?? default);
    }

    public static void Marcar(Activity? atividade, string sistema, Guid? eventId, Guid? ordemServicoId)
    {
        if (atividade is null) return;
        atividade.SetTag("messaging.system", sistema);
        if (eventId is not null) atividade.SetTag("messaging.message.id", eventId.Value.ToString());
        if (ordemServicoId is not null) atividade.SetTag("ordem_servico.id", ordemServicoId.Value.ToString());
    }

    private static bool TentarLerTexto(JsonElement raiz, string nome, out string? valor)
    {
        valor = null;
        return raiz.TryGetProperty(nome, out var elemento) && LeitorJson.TentarLerString(elemento, out valor);
    }
}

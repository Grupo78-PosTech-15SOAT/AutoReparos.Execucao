using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.Execution.Application;
using AutoReparos.Execution.Domain;
using AutoReparos.Execution.Tests.Suporte;
using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Execution.Tests;

[Collection("postgres")]
public sealed class ConclusaoExecucaoTests(PostgresFixture postgres)
{
    private static string Conclusao(Guid id) => $"/api/v1/execucoes/{id}/conclusao";

    [Fact]
    public async Task ExecutionCompletion_WritesOneOutboxRowPerConsumerWithSameEventId()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o => o.RegistrarEtapa(TipoEtapa.Reparo, "Troca de óleo", "mec-1", DateTimeOffset.UtcNow));
        var momento = new DateTimeOffset(2026, 11, 13, 17, 30, 0, TimeSpan.Zero);
        ambiente.Relogio.Agora = momento;
        using var http = ambiente.Fabrica.Cliente("Mecanico", "mec-1");

        var resposta = await http.PostAsync(Conclusao(ordem.Id), null);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Concluida", corpo.GetProperty("status").GetString());
        Assert.Equal(momento, corpo.GetProperty("concluidaEm").GetDateTimeOffset());
        var salva = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.SingleAsync());
        Assert.Equal((EstadoExecucao.Concluida, momento), (salva.Estado, salva.ConcluidaEm));
        var saida = (await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.ToListAsync())).OrderBy(m => m.Destino).ToList();
        Assert.Equal([DestinosEvento.Billing, DestinosEvento.OsService], saida.Select(m => m.Destino).ToArray());
        Assert.All(saida, m =>
        {
            Assert.Equal("execucao.concluida.v1", m.Tipo);
            Assert.Null(m.EnviadoEm);
            var evento = JsonDocument.Parse(m.Conteudo).RootElement;
            Assert.Equal(m.EventId, evento.GetProperty("eventId").GetGuid());
            Assert.Equal("execucao.concluida.v1", evento.GetProperty("tipo").GetString());
            Assert.Equal(ordem.Id, evento.GetProperty("id").GetGuid());
            Assert.Equal(ordem.OrdemServicoId, evento.GetProperty("ordemServicoId").GetGuid());
            Assert.Equal(ordem.ClienteId, evento.GetProperty("clienteId").GetGuid());
            Assert.Equal(ordem.VeiculoId, evento.GetProperty("veiculoId").GetGuid());
            Assert.Equal(momento, evento.GetProperty("concluidaEm").GetDateTimeOffset());
        });
        Assert.Equal(saida[0].EventId, saida[1].EventId);
    }

    [Fact]
    public async Task ExecutionCompletion_AuthorizationNotFoundAndStateRules()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var naFila = await ambiente.Fabrica.SemearAsync();
        var emExecucao = await ambiente.Fabrica.SemearAsync(o => o.RegistrarEtapa(TipoEtapa.Reparo, "Reparo", "mec-1", DateTimeOffset.UtcNow));
        using var anonimo = ambiente.Fabrica.Cliente(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsync(Conclusao(emExecucao.Id), null)).StatusCode);
        using var atendente = ambiente.Fabrica.Cliente("Atendente");
        Assert.Equal(HttpStatusCode.Forbidden, (await atendente.PostAsync(Conclusao(emExecucao.Id), null)).StatusCode);
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico");
        Assert.Equal(HttpStatusCode.NotFound, (await mecanico.PostAsync(Conclusao(Guid.NewGuid()), null)).StatusCode);
        // Sem nenhuma etapa registrada a execução ainda está na fila e não pode ser concluída.
        var naoIniciada = await mecanico.PostAsync(Conclusao(naFila.Id), null);
        Assert.Equal(HttpStatusCode.Conflict, naoIniciada.StatusCode);
        Assert.Equal("execucao_estado_invalido", (await naoIniciada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.CountAsync()));
        Assert.Equal(HttpStatusCode.OK, (await mecanico.PostAsync(Conclusao(emExecucao.Id), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mecanico.PostAsync(Conclusao(emExecucao.Id), null)).StatusCode);
        Assert.Equal(2, await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.CountAsync()));
    }

    [Fact]
    public async Task ExecutionCompletion_ConcurrentRequestsPublishOnce()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o => o.RegistrarEtapa(TipoEtapa.Reparo, "Reparo", "mec-1", DateTimeOffset.UtcNow));
        using var a = ambiente.Fabrica.Cliente(); using var b = ambiente.Fabrica.Cliente();
        ambiente.Gate.Armar(2);

        var respostas = await Task.WhenAll(a.PostAsync(Conclusao(ordem.Id), null), b.PostAsync(Conclusao(ordem.Id), null));
        ambiente.Gate.Armar(0);

        Assert.Equal([200, 409], respostas.Select(r => (int)r.StatusCode).Order().ToArray());
        Assert.Equal(2, await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.CountAsync()));
    }
}

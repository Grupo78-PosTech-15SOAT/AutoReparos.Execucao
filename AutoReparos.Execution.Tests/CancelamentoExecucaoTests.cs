using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoReparos.Execution.Application;
using AutoReparos.Execution.Domain;
using AutoReparos.Execution.Infrastructure;
using AutoReparos.Execution.Tests.Suporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutoReparos.Execution.Tests;

// Saga: execucao.cancelar.v1 na fila de entrada, respostas ao OS Service pelo outbox e correlação nas mensagens de saída.
[Collection("postgres")]
public sealed class CancelamentoExecucaoTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private const string AgoraJson = "2026-11-13T12:00:00+00:00";

    private static async Task<List<MensagemSaida>> SaidaAsync(Ambiente ambiente) =>
        (await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.AsNoTracking().ToListAsync())).OrderBy(m => m.CriadoEm).ThenBy(m => m.Tipo).ToList();

    private static string Esperado(MensagemSaida m, string tipo, string? correlacao, Guid os, Guid? execucao, string? motivo) =>
        $"{{\"eventId\":\"{m.EventId}\",\"tipo\":\"{tipo}\",\"correlacaoId\":{(correlacao is null ? "null" : $"\"{correlacao}\"")},\"ordemServicoId\":\"{os}\",\"ocorreuEm\":\"{AgoraJson}\""
        + (execucao is null ? "" : $",\"execucaoId\":\"{execucao}\"") + (motivo is null ? "" : $",\"motivo\":{JsonSerializer.Serialize(motivo)}") + "}";

    private static void AssertEntregaSemAlerta(Ambiente ambiente, Amazon.SQS.Model.Message mensagem)
    {
        Assert.Equal([(FabricaApi.FilaLiberadas, mensagem.ReceiptHandle)], ambiente.Sqs.Removidas);
        Assert.Empty(ambiente.Sqs.Enviadas);
        Assert.Empty(ambiente.Alertas.Itens);
    }

    [Fact]
    public async Task ExecutionCancellation_CancelsQueuedExecutionAndRepliesThroughOutbox()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        Guid eventId = Guid.NewGuid(), correlacao = Guid.NewGuid();
        var mensagem = Mensagens.Cancelar(eventId, ordem.OrdemServicoId, "Orçamento cancelado pelo cliente", correlacao);

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        var salva = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync());
        Assert.Equal((EstadoExecucao.Cancelada, "Orçamento cancelado pelo cliente", (DateTimeOffset?)Agora), (salva.Estado, salva.MotivoCancelamento, salva.CanceladaEm));
        Assert.Equal(Agora, salva.AtualizadaEm);
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.Where(e => e.EventId == eventId).CountAsync()));
        var saida = Assert.Single(await SaidaAsync(ambiente));
        Assert.Equal((DestinosEvento.OsService, "execucao.cancelada.v1", (DateTimeOffset?)null), (saida.Destino, saida.Tipo, saida.EnviadoEm));
        Assert.Equal(Esperado(saida, "execucao.cancelada.v1", correlacao.ToString(), ordem.OrdemServicoId, ordem.Id, null), saida.Conteudo);
        AssertEntregaSemAlerta(ambiente, mensagem);
    }

    [Fact]
    public async Task ExecutionCancellation_CancelsInProgressExecutionKeepingRecordedStages()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o =>
        {
            o.RegistrarEtapa(TipoEtapa.Diagnostico, "Checar suspensão", "mec-1", DateTimeOffset.UtcNow);
            o.RegistrarEtapa(TipoEtapa.Reparo, "Trocar amortecedor", "mec-2", DateTimeOffset.UtcNow);
        });
        var mensagem = Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId);

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        var salva = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().Include(o => o.Etapas).SingleAsync());
        Assert.Equal(EstadoExecucao.Cancelada, salva.Estado);
        Assert.Equal([(1, "Checar suspensão", "mec-1"), (2, "Trocar amortecedor", "mec-2")], salva.Etapas.OrderBy(e => e.Sequencia).Select(e => (e.Sequencia, e.Descricao, e.Responsavel)).ToArray());
        var saida = Assert.Single(await SaidaAsync(ambiente));
        Assert.Equal("execucao.cancelada.v1", saida.Tipo);
        Assert.Equal(Esperado(saida, "execucao.cancelada.v1", null, ordem.OrdemServicoId, ordem.Id, null), saida.Conteudo);
        AssertEntregaSemAlerta(ambiente, mensagem);
    }

    [Fact]
    public async Task ExecutionCancellation_CorrelationFallsBackToTheOneStoredWhenCommandOmitsIt()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        Guid os = Guid.NewGuid(), correlacao = Guid.NewGuid();
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), os, correlacaoId: correlacao), CancellationToken.None);

        await ambiente.Consumidor.ProcessarAsync(Mensagens.Cancelar(Guid.NewGuid(), os), CancellationToken.None);

        var saida = Assert.Single(await SaidaAsync(ambiente));
        Assert.Equal(correlacao, JsonDocument.Parse(saida.Conteudo).RootElement.GetProperty("correlacaoId").GetGuid());
    }

    [Fact]
    public async Task ExecutionCancellation_RefusesCompletedExecutionWithoutChangingIt()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o => { o.RegistrarEtapa(TipoEtapa.Reparo, "Troca de óleo", "mec-1", DateTimeOffset.UtcNow); o.Concluir(DateTimeOffset.UtcNow); });
        var antes = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync());
        Guid eventId = Guid.NewGuid(), correlacao = Guid.NewGuid();
        var mensagem = Mensagens.Cancelar(eventId, ordem.OrdemServicoId, correlacaoId: correlacao);

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        var depois = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync());
        Assert.Equal((EstadoExecucao.Concluida, antes.Revisao, (string?)null, (DateTimeOffset?)null), (depois.Estado, depois.Revisao, depois.MotivoCancelamento, depois.CanceladaEm));
        var saida = Assert.Single(await SaidaAsync(ambiente));
        Assert.Equal("execucao.cancelamento-recusado.v1", saida.Tipo);
        Assert.Equal(Esperado(saida, "execucao.cancelamento-recusado.v1", correlacao.ToString(), ordem.OrdemServicoId, ordem.Id, "Execução já concluída não pode ser cancelada."), saida.Conteudo);
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.Where(e => e.EventId == eventId).CountAsync()));
        AssertEntregaSemAlerta(ambiente, mensagem);
    }

    [Fact]
    public async Task ExecutionCancellation_CancelsFailedExecutionKeepingStagesAndBlocksReexecution()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o =>
        {
            o.RegistrarEtapa(TipoEtapa.Diagnostico, "Checar suspensão", "mec-1", DateTimeOffset.UtcNow);
            o.RegistrarEtapa(TipoEtapa.Reparo, "Trocar amortecedor", "mec-2", DateTimeOffset.UtcNow);
            o.Falhar("Peça quebrada", DateTimeOffset.UtcNow);
        });
        Guid correlacao = Guid.NewGuid();

        await ambiente.Consumidor.ProcessarAsync(Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId, "Orçamento cancelado pelo cliente", correlacao), CancellationToken.None);

        var salva = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().Include(o => o.Etapas).SingleAsync());
        Assert.Equal((EstadoExecucao.Cancelada, "Orçamento cancelado pelo cliente", (DateTimeOffset?)Agora), (salva.Estado, salva.MotivoCancelamento, salva.CanceladaEm));
        Assert.Equal([(1, "Checar suspensão", "mec-1"), (2, "Trocar amortecedor", "mec-2")], salva.Etapas.OrderBy(e => e.Sequencia).Select(e => (e.Sequencia, e.Descricao, e.Responsavel)).ToArray());
        var saida = Assert.Single(await SaidaAsync(ambiente));
        Assert.Equal("execucao.cancelada.v1", saida.Tipo);
        Assert.Equal(Esperado(saida, "execucao.cancelada.v1", correlacao.ToString(), ordem.OrdemServicoId, ordem.Id, null), saida.Conteudo);

        using var supervisor = ambiente.Fabrica.Cliente("Administrador");
        var resposta = await supervisor.PostAsJsonAsync($"/api/v1/execucoes/{ordem.Id}/reexecucao", new { motivo = "Tentar de novo" });
        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal("execucao_estado_invalido", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(EstadoExecucao.Cancelada, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync())).Estado);
    }

    [Fact]
    public async Task ExecutionCancellation_RefusesUnknownOrderOmittingExecutionId()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        Guid eventId = Guid.NewGuid(), os = Guid.NewGuid(), correlacao = Guid.NewGuid();
        var mensagem = Mensagens.Cancelar(eventId, os, correlacaoId: correlacao);

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync()));
        var saida = Assert.Single(await SaidaAsync(ambiente));
        Assert.Equal(Esperado(saida, "execucao.cancelamento-recusado.v1", correlacao.ToString(), os, null, "Execução da OS não encontrada."), saida.Conteudo);
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.Where(e => e.EventId == eventId).CountAsync()));
        AssertEntregaSemAlerta(ambiente, mensagem);
    }

    [Fact]
    public async Task ExecutionCancellation_RepeatedCancellationWithNewEventConfirmsAgainKeepingFirstReason()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId, "Primeiro motivo"), CancellationToken.None);
        var revisao = (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync())).Revisao;

        await ambiente.Consumidor.ProcessarAsync(Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId, "Segundo motivo"), CancellationToken.None);

        var salva = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync());
        Assert.Equal(("Primeiro motivo", revisao), (salva.MotivoCancelamento, salva.Revisao));
        Assert.Equal(["execucao.cancelada.v1", "execucao.cancelada.v1"], (await SaidaAsync(ambiente)).Select(m => m.Tipo).ToArray());
    }

    [Fact]
    public async Task ExecutionCancellation_RepeatedEventIsDeduplicatedByEventId()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        var mensagem = Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId);

        for (var i = 0; i < 3; i++) await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        Assert.Single(await SaidaAsync(ambiente));
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.CountAsync()));
        Assert.Equal(3, ambiente.Sqs.Removidas.Count);
        Assert.Empty(ambiente.Sqs.Enviadas);
        Assert.Empty(ambiente.Alertas.Itens);
    }

    [Fact]
    public async Task ExecutionCancellation_RepeatedEventAfterRefusalDoesNotRepeatTheReply()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var mensagem = Mensagens.Cancelar(Guid.NewGuid(), Guid.NewGuid());

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);
        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        Assert.Equal(["execucao.cancelamento-recusado.v1"], (await SaidaAsync(ambiente)).Select(m => m.Tipo).ToArray());
    }

    [Fact]
    public async Task ExecutionCancellation_ConcurrentDeliveriesOfSameEventCancelOnce()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        var mensagem = Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId);
        ambiente.Gate.Armar(2);

        await Task.WhenAll(ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None), ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None));
        ambiente.Gate.Armar(0);

        Assert.Equal(EstadoExecucao.Cancelada, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync())).Estado);
        Assert.Equal(["execucao.cancelada.v1"], (await SaidaAsync(ambiente)).Select(m => m.Tipo).ToArray());
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.CountAsync()));
        Assert.Equal(2, ambiente.Sqs.Removidas.Count);
        Assert.Empty(ambiente.Sqs.Enviadas);
        Assert.Empty(ambiente.Alertas.Itens);
    }

    [Fact]
    public async Task ExecutionCancellation_ConcurrentStageRegistrationNeverLosesTheCancellation()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        using var http = ambiente.Fabrica.Cliente("Mecanico", "mec-1");
        ambiente.Gate.Armar(2);

        var etapa = http.PostAsJsonAsync($"/api/v1/execucoes/{ordem.Id}/etapas", new { tipo = "Reparo", descricao = "Troca de pastilhas" });
        var cancelamento = ambiente.Consumidor.ProcessarAsync(Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId), CancellationToken.None);
        await Task.WhenAll(etapa, cancelamento);
        var statusEtapa = (await etapa).StatusCode;
        ambiente.Gate.Armar(0);

        var salva = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().Include(o => o.Etapas).SingleAsync());
        Assert.Equal(EstadoExecucao.Cancelada, salva.Estado);
        // A etapa aceita (201) fica preservada; a perdedora pela concorrência (409) não grava nada.
        Assert.Equal(statusEtapa == HttpStatusCode.Created ? 1 : 0, salva.Etapas.Count);
        Assert.Contains(statusEtapa, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
        Assert.Equal(["execucao.cancelada.v1"], (await SaidaAsync(ambiente)).Select(m => m.Tipo).ToArray());
        Assert.Empty(ambiente.Alertas.Itens);
    }

    public static IEnumerable<object[]> Malformadas()
    {
        const string os = "b3d5f7a1-1c2e-4f60-8a9b-0c1d2e3f4a5b", ev = "6f1c2a3e-0b7d-4c55-9a1e-2d8f4b6c7a90";
        yield return [$"{{\"eventId\":\"{ev}\",\"tipo\":\"execucao.cancelar.v1\",\"ordemServicoId\":\"{os}\"}}"];
        yield return [$"{{\"eventId\":\"{ev}\",\"tipo\":\"execucao.cancelar.v1\",\"ordemServicoId\":\"{os}\",\"motivo\":\"  \"}}"];
        yield return [$"{{\"eventId\":\"{ev}\",\"tipo\":\"execucao.cancelar.v1\",\"motivo\":\"x\"}}"];
        yield return [$"{{\"eventId\":\"{ev}\",\"tipo\":\"execucao.cancelar.v1\",\"ordemServicoId\":\"{os.Replace("-", "")}\",\"motivo\":\"x\"}}"];
        yield return [$"{{\"eventId\":\"00000000-0000-0000-0000-000000000000\",\"tipo\":\"execucao.cancelar.v1\",\"ordemServicoId\":\"{os}\",\"motivo\":\"x\"}}"];
        yield return [$"{{\"tipo\":\"execucao.cancelar.v1\",\"ordemServicoId\":\"{os}\",\"motivo\":\"x\"}}"];
        yield return [$"{{\"eventId\":\"{ev}\",\"tipo\":\"execucao.cancelar.v1\",\"ordemServicoId\":\"{os}\",\"motivo\":\"x\",\"correlacaoId\":\"abc\"}}"];
        yield return [$"{{\"eventId\":\"{ev}\",\"tipo\":\"execucao.cancelar.v1\",\"ordemServicoId\":\"{os}\",\"motivo\":\"a\\u0000b\"}}"];
        yield return [$"{{\"eventId\":\"{ev}\",\"tipo\":\"execucao.cancelar.v2\",\"ordemServicoId\":\"{os}\",\"motivo\":\"x\"}}"];
        yield return [$"{{\"eventId\":\"{ev}\",\"tipo\":\"ordem-servico.cancelar.v1\",\"ordemServicoId\":\"{os}\",\"motivo\":\"x\"}}"];
        yield return [$"{{\"eventId\":\"{ev}\",\"ordemServicoId\":\"{os}\",\"motivo\":\"x\"}}"];
        yield return [$"{{\"eventId\":\"{ev}\",\"tipo\":\"ordem-servico.liberada.v1\",\"id\":\"{os}\",\"clienteId\":\"0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d\",\"veiculoId\":\"5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a8b9\",\"correlacaoId\":\"x\"}}"];
    }

    [Theory]
    [MemberData(nameof(Malformadas))]
    public async Task ExecutionCancellation_MalformedOrUnknownMessageGoesToDeadLetterWithAlert(string corpo)
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var mensagem = new Amazon.SQS.Model.Message { MessageId = "m-1", ReceiptHandle = "r-1", Body = corpo };

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync()));
        Assert.Equal(0, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.CountAsync()));
        Assert.Empty(await SaidaAsync(ambiente));
        Assert.Equal([(FabricaApi.DlqLiberadas, corpo)], ambiente.Sqs.Enviadas);
        Assert.Equal([("execucao.evento_invalido", "m-1")], ambiente.Alertas.Itens);
        Assert.Equal([(FabricaApi.FilaLiberadas, "r-1")], ambiente.Sqs.Removidas);
    }

    [Fact]
    public async Task ExecutionCancellation_MalformedCommandLeavesAnExistingExecutionUntouched()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        var corpo = $"{{\"eventId\":\"{Guid.NewGuid()}\",\"tipo\":\"execucao.cancelar.v1\",\"ordemServicoId\":\"{ordem.OrdemServicoId}\"}}";

        await ambiente.Consumidor.ProcessarAsync(new Amazon.SQS.Model.Message { MessageId = "m-2", ReceiptHandle = "r-2", Body = corpo }, CancellationToken.None);

        Assert.Equal(EstadoExecucao.Fila, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync())).Estado);
        Assert.Single(ambiente.Alertas.Itens);
    }

    [Fact]
    public async Task ExecutionCancellation_ReplyIsDispatchedToTheOsServiceQueueWithTheExactEnvelope()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId), CancellationToken.None);
        var linha = Assert.Single(await SaidaAsync(ambiente));

        Assert.True(await ambiente.Despachante.DespacharProximaAsync(CancellationToken.None));
        Assert.False(await ambiente.Despachante.DespacharProximaAsync(CancellationToken.None));

        Assert.Equal([(FabricaApi.FilaOs, linha.Conteudo)], ambiente.Sqs.Enviadas);
        Assert.NotNull((await SaidaAsync(ambiente)).Single().EnviadoEm);
    }

    // Liberação recusada por regra de negócio

    [Theory]
    [InlineData(false, "Execução da OS já foi cancelada.")]
    [InlineData(true, "Execução da OS já foi concluída.")]
    public async Task ReleaseRefusal_ReleaseForFinishedExecutionIsRefusedToTheOsService(bool concluida, string motivo)
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o =>
        {
            if (concluida) { o.RegistrarEtapa(TipoEtapa.Reparo, "Reparo", "mec-1", DateTimeOffset.UtcNow); o.Concluir(DateTimeOffset.UtcNow); }
            else o.Cancelar("Cancelada antes", DateTimeOffset.UtcNow);
        });
        Guid eventId = Guid.NewGuid(), correlacao = Guid.NewGuid();
        var mensagem = Mensagens.Liberada(eventId, ordem.OrdemServicoId, correlacaoId: correlacao);

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        var saida = Assert.Single(await SaidaAsync(ambiente));
        Assert.Equal((DestinosEvento.OsService, "execucao.liberacao-recusada.v1"), (saida.Destino, saida.Tipo));
        Assert.Equal(Esperado(saida, "execucao.liberacao-recusada.v1", correlacao.ToString(), ordem.OrdemServicoId, null, motivo), saida.Conteudo);
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync()));
        Assert.Equal(concluida ? EstadoExecucao.Concluida : EstadoExecucao.Cancelada, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync())).Estado);
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.EventosProcessados.Where(e => e.EventId == eventId).CountAsync()));
        AssertEntregaSemAlerta(ambiente, mensagem);
    }

    [Fact]
    public async Task ReleaseRefusal_ReleaseAfterCancellationCommandIsRefusedAndRedeliveryDoesNotRepeatIt()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId), CancellationToken.None);
        var liberada = Mensagens.Liberada(Guid.NewGuid(), ordem.OrdemServicoId);

        await ambiente.Consumidor.ProcessarAsync(liberada, CancellationToken.None);
        await ambiente.Consumidor.ProcessarAsync(liberada, CancellationToken.None);

        Assert.Equal(["execucao.cancelada.v1", "execucao.liberacao-recusada.v1"], (await SaidaAsync(ambiente)).Select(m => m.Tipo).Order().ToArray());
        Assert.Equal(1, await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.CountAsync()));
    }

    [Fact]
    public async Task ReleaseRefusal_ReleaseForExecutionStillOpenIsNotRefused()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o => o.RegistrarEtapa(TipoEtapa.Reparo, "Reparo", "mec-1", DateTimeOffset.UtcNow));

        await ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), ordem.OrdemServicoId), CancellationToken.None);

        Assert.Empty(await SaidaAsync(ambiente));
        Assert.Equal(EstadoExecucao.EmExecucao, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync())).Estado);
    }

    [Fact]
    public async Task ReleaseRefusal_StructurallyInvalidReleaseGoesOnlyToDeadLetter()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync(o => o.Cancelar("Cancelada antes", DateTimeOffset.UtcNow));
        var corpo = $"{{\"eventId\":\"{Guid.NewGuid()}\",\"tipo\":\"ordem-servico.liberada.v1\",\"id\":\"{ordem.OrdemServicoId}\"}}";

        await ambiente.Consumidor.ProcessarAsync(new Amazon.SQS.Model.Message { MessageId = "m-3", ReceiptHandle = "r-3", Body = corpo }, CancellationToken.None);

        Assert.Empty(await SaidaAsync(ambiente));
        Assert.Equal([(FabricaApi.DlqLiberadas, corpo)], ambiente.Sqs.Enviadas);
        Assert.Equal([("execucao.evento_invalido", "m-3")], ambiente.Alertas.Itens);
    }

    // Rotas e fila para uma execução cancelada

    private static async Task<OrdemExecucao> SemearCanceladaAsync(Ambiente ambiente) =>
        await ambiente.Fabrica.SemearAsync(o =>
        {
            o.RegistrarEtapa(TipoEtapa.Diagnostico, "Checar suspensão", "mec-1", DateTimeOffset.UtcNow);
            o.Cancelar("Cliente desistiu", DateTimeOffset.UtcNow);
        });

    [Fact]
    public async Task ExecutionCancellation_CancelledExecutionRejectsStagesCompletionFailureAndReexecutionWith409()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await SemearCanceladaAsync(ambiente);
        var antes = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().Include(o => o.Etapas).SingleAsync());
        using var mecanico = ambiente.Fabrica.Cliente("Mecanico", "mec-1");
        using var admin = ambiente.Fabrica.Cliente("Administrador", "sup-1");
        var base_ = $"/api/v1/execucoes/{ordem.Id}";

        var respostas = new[]
        {
            await mecanico.PostAsJsonAsync($"{base_}/etapas", new { tipo = "Reparo", descricao = "Tarde demais" }),
            await mecanico.PostAsync($"{base_}/conclusao", null),
            await mecanico.PostAsJsonAsync($"{base_}/falha", new { motivo = "Tarde demais" }),
            await admin.PostAsJsonAsync($"{base_}/reexecucao", new { motivo = "Tarde demais" })
        };

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        foreach (var r in respostas) Assert.Equal("execucao_estado_invalido", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        var depois = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().Include(o => o.Etapas).SingleAsync());
        Assert.Equal((EstadoExecucao.Cancelada, antes.Revisao, 1, 0), (depois.Estado, depois.Revisao, depois.Etapas.Count, await ambiente.Fabrica.NoBancoAsync(db => db.MensagensSaida.CountAsync())));
    }

    [Fact]
    public async Task ExecutionCancellation_DefaultQueueOmitsCancelledExecutionsButStatusFilterShowsThem()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var fila = await ambiente.Fabrica.SemearAsync();
        var emExecucao = await ambiente.Fabrica.SemearAsync(o => o.RegistrarEtapa(TipoEtapa.Reparo, "Reparo", "mec-1", DateTimeOffset.UtcNow));
        var falhou = await ambiente.Fabrica.SemearAsync(o => o.Falhar("Peça quebrada", DateTimeOffset.UtcNow));
        var cancelada = await SemearCanceladaAsync(ambiente);
        using var http = ambiente.Fabrica.Cliente("Mecanico");

        var padrao = await http.GetFromJsonAsync<JsonElement>("/api/v1/fila-execucao");
        var soCanceladas = await http.GetFromJsonAsync<JsonElement>("/api/v1/fila-execucao?status=Cancelada");

        Assert.Equal(3, padrao.GetProperty("total").GetInt32());
        Assert.Equal(new[] { fila.Id, emExecucao.Id, falhou.Id }.Order().ToArray(), padrao.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Order().ToArray());
        Assert.DoesNotContain("Cancelada", padrao.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("status").GetString()));
        var item = Assert.Single(soCanceladas.GetProperty("itens").EnumerateArray());
        Assert.Equal((cancelada.Id, "Cancelada", "Cliente desistiu"), (item.GetProperty("id").GetGuid(), item.GetProperty("status").GetString(), item.GetProperty("motivoCancelamento").GetString()));
        Assert.Single(item.GetProperty("etapas").EnumerateArray());
    }

    // Correlação nas mensagens de saída

    [Fact]
    public async Task SagaCorrelation_CompletionCarriesCorrelationAndOrderToBothConsumers()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        Guid os = Guid.NewGuid(), correlacao = Guid.NewGuid();
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), os, correlacaoId: correlacao), CancellationToken.None);
        var execucaoId = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.Select(o => o.Id).SingleAsync());
        using var http = ambiente.Fabrica.Cliente("Mecanico", "mec-1");
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsJsonAsync($"/api/v1/execucoes/{execucaoId}/etapas", new { tipo = "Reparo", descricao = "Reparo" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"/api/v1/execucoes/{execucaoId}/conclusao", null)).StatusCode);

        var saida = await SaidaAsync(ambiente);
        Assert.Equal([DestinosEvento.Billing, DestinosEvento.OsService], saida.Select(m => m.Destino).Order().ToArray());
        Assert.All(saida, m =>
        {
            var evento = JsonDocument.Parse(m.Conteudo).RootElement;
            Assert.Equal(correlacao, evento.GetProperty("correlacaoId").GetGuid());
            Assert.Equal(os, evento.GetProperty("ordemServicoId").GetGuid());
            Assert.Equal(execucaoId, evento.GetProperty("id").GetGuid());
            Assert.Equal(AgoraJson, evento.GetProperty("ocorreuEm").GetString());
        });
    }

    [Fact]
    public async Task SagaCorrelation_FailureCarriesCorrelationAndOrder()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        Guid os = Guid.NewGuid(), correlacao = Guid.NewGuid();
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), os, correlacaoId: correlacao), CancellationToken.None);
        var execucaoId = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.Select(o => o.Id).SingleAsync());
        using var http = ambiente.Fabrica.Cliente("Mecanico", "mec-1");

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"/api/v1/execucoes/{execucaoId}/falha", new { motivo = "Peça quebrada" })).StatusCode);

        var saida = Assert.Single(await SaidaAsync(ambiente));
        var evento = JsonDocument.Parse(saida.Conteudo).RootElement;
        Assert.Equal((DestinosEvento.OsService, "execucao.falhou.v1", correlacao, os, execucaoId, "Peça quebrada"),
            (saida.Destino, evento.GetProperty("tipo").GetString(), evento.GetProperty("correlacaoId").GetGuid(), evento.GetProperty("ordemServicoId").GetGuid(), evento.GetProperty("id").GetGuid(), evento.GetProperty("motivo").GetString()));
    }

    [Fact]
    public async Task SagaCorrelation_AbsentCorrelationStaysNullAndKeepsTheCurrentContract()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var ordem = await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync());
        using var http = ambiente.Fabrica.Cliente("Mecanico", "mec-1");

        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync($"/api/v1/execucoes/{ordem.Id}/falha", new { motivo = "Peça quebrada" })).StatusCode);

        Assert.Null(ordem.CorrelacaoId);
        var evento = JsonDocument.Parse(Assert.Single(await SaidaAsync(ambiente)).Conteudo).RootElement;
        Assert.Equal(JsonValueKind.Null, evento.GetProperty("correlacaoId").ValueKind);
        Assert.Equal(["eventId", "tipo", "correlacaoId", "ocorreuEm", "id", "ordemServicoId", "clienteId", "veiculoId", "motivo", "falhouEm"],
            evento.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task SagaCorrelation_ReleaseStoresTheCorrelationAndRepeatedReleaseDoesNotOverwriteIt()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        Guid os = Guid.NewGuid(), primeira = Guid.NewGuid();
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), os, correlacaoId: primeira), CancellationToken.None);

        await ambiente.Consumidor.ProcessarAsync(Mensagens.Liberada(Guid.NewGuid(), os, correlacaoId: Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(primeira, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync())).CorrelacaoId);
        Assert.Empty(await SaidaAsync(ambiente));
    }

    // Retry, DLQ e alerta no despacho das novas mensagens

    private sealed class Contador { public int Chamadas; }

    // Delega ao banco real, mas falha nas primeiras leituras de eventos.
    private sealed class ArmazenamentoInstavel(ExecucaoDbContext real, Contador contador, int falhas, Func<Exception> erro) : IExecucaoStore
    {
        public Task<bool> EventoProcessadoAsync(Guid eventId, CancellationToken ct)
        {
            if (Interlocked.Increment(ref contador.Chamadas) <= falhas) throw erro();
            return real.EventoProcessadoAsync(eventId, ct);
        }
        public Task<OrdemExecucao?> ObterAsync(Guid id, CancellationToken ct) => real.ObterAsync(id, ct);
        public Task<IReadOnlyList<OrdemExecucao>> ListarFilaAsync(IReadOnlyCollection<EstadoExecucao> estados, string? mecanico, CancellationToken ct) => real.ListarFilaAsync(estados, mecanico, ct);
        public Task<OrdemExecucao?> ObterPorOrdemServicoAsync(Guid ordemServicoId, CancellationToken ct) => real.ObterPorOrdemServicoAsync(ordemServicoId, ct);
        public void Adicionar(OrdemExecucao ordem) => real.Adicionar(ordem);
        public void Adicionar(EventoProcessado evento) => real.Adicionar(evento);
        public void Adicionar(MensagemSaida mensagem) => real.Adicionar(mensagem);
        public void DescartarAlteracoes() => real.DescartarAlteracoes();
        public Task SalvarAsync(CancellationToken ct) => real.SalvarAsync(ct);
    }

    private async Task<(Ambiente Ambiente, Contador Contador)> AmbienteInstavelAsync(int falhas, Func<Exception> erro)
    {
        var contador = new Contador();
        var ambiente = await Ambiente.CriarAsync(postgres, ajustar: services => services.AddScoped<IExecucaoStore>(sp =>
            new ArmazenamentoInstavel(sp.GetRequiredService<ExecucaoDbContext>(), contador, falhas, erro)));
        return (ambiente, contador);
    }

    private static Exception FalhaDeRede() => new NpgsqlException("conexão perdida", new IOException("reset"));

    [Fact]
    public async Task ExecutionCancellation_DatabaseDownExhaustsFiveAttemptsThenDeadLettersWithAlert()
    {
        var (ambiente, contador) = await AmbienteInstavelAsync(int.MaxValue, FalhaDeRede);
        using var liberar = ambiente;
        var eventId = Guid.NewGuid(); var mensagem = Mensagens.Cancelar(eventId, Guid.NewGuid());

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        Assert.Equal(5, contador.Chamadas);
        Assert.Equal(4, ambiente.Esperas.Itens.Count);
        Assert.Equal([(FabricaApi.DlqLiberadas, mensagem.Body)], ambiente.Sqs.Enviadas);
        var alerta = Assert.Single(ambiente.Alertas.Itens);
        Assert.Equal("execucao.banco.exhausted", alerta.Codigo);
        Assert.Contains(eventId.ToString(), alerta.Detalhe);
        Assert.Equal([(FabricaApi.FilaLiberadas, mensagem.ReceiptHandle)], ambiente.Sqs.Removidas);
        Assert.Empty(await SaidaAsync(ambiente));
    }

    [Fact]
    public async Task ExecutionCancellation_DatabaseRecoveryBeforeTheLimitCancelsWithoutDeadLetter()
    {
        var (ambiente, contador) = await AmbienteInstavelAsync(2, () => new TimeoutException("banco lento"));
        using var liberar = ambiente;
        var ordem = await ambiente.Fabrica.SemearAsync();
        var mensagem = Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId);

        await ambiente.Consumidor.ProcessarAsync(mensagem, CancellationToken.None);

        Assert.Equal(3, contador.Chamadas);
        Assert.Equal(2, ambiente.Esperas.Itens.Count);
        Assert.Equal(EstadoExecucao.Cancelada, (await ambiente.Fabrica.NoBancoAsync(db => db.Ordens.AsNoTracking().SingleAsync())).Estado);
        Assert.Single(await SaidaAsync(ambiente));
        AssertEntregaSemAlerta(ambiente, mensagem);
    }

    [Fact]
    public async Task ExecutionCancellation_ReplyGoesToDeadLetterAndAlertsWhenTheOsQueueStaysDown()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        var ordem = await ambiente.Fabrica.SemearAsync();
        await ambiente.Consumidor.ProcessarAsync(Mensagens.Cancelar(Guid.NewGuid(), ordem.OrdemServicoId), CancellationToken.None);
        var linha = Assert.Single(await SaidaAsync(ambiente));
        ambiente.Sqs.FalhaEnvio = (fila, _) => fila == FabricaApi.FilaOs ? new InvalidOperationException("fila indisponível") : null;

        Assert.True(await ambiente.Despachante.DespacharProximaAsync(CancellationToken.None));

        Assert.Equal(5, ambiente.Sqs.Tentativas(FabricaApi.FilaOs));
        Assert.Equal([(FabricaApi.DlqSaida, linha.Conteudo)], ambiente.Sqs.Enviadas);
        Assert.Equal([("execucao.sqs.exhausted", $"{DestinosEvento.OsService}/{linha.EventId}")], ambiente.Alertas.Itens);
        var salva = Assert.Single(await SaidaAsync(ambiente));
        Assert.True(salva.EncaminhadoParaDlq);
        Assert.NotNull(salva.EnviadoEm);
    }
}

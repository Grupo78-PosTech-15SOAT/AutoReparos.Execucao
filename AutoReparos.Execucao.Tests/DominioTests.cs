using AutoReparos.Execucao.Application;
using AutoReparos.Execucao.Domain;

namespace AutoReparos.Execucao.Tests;

public sealed class DominioTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);
    private static OrdemExecucao Nova() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), T0);

    [Fact]
    public void NewExecutionStartsInQueueWithoutIntervention()
    {
        var ordem = Nova();
        Assert.Equal(EstadoExecucao.Fila, ordem.Estado);
        Assert.False(ordem.IntervencaoRequerida);
        Assert.Empty(ordem.Etapas);
        Assert.Equal(T0, ordem.CriadaEm);
    }

    [Fact]
    public void FirstStageMovesToInProgressAndSequenceIncreases()
    {
        var ordem = Nova();
        var primeira = ordem.RegistrarEtapa(TipoEtapa.Diagnostico, "  Checar óleo  ", " mec-1 ", T0.AddMinutes(1));
        var segunda = ordem.RegistrarEtapa(TipoEtapa.Reparo, "Trocar filtro", "mec-2", T0.AddMinutes(2));
        Assert.Equal(EstadoExecucao.EmExecucao, ordem.Estado);
        Assert.Equal((1, "Checar óleo", "mec-1"), (primeira.Sequencia, primeira.Descricao, primeira.Responsavel));
        Assert.Equal(2, segunda.Sequencia);
        Assert.Equal(2, ordem.Revisao);
        Assert.Equal(T0.AddMinutes(2), ordem.AtualizadaEm);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\0b")]
    public void InvalidDescriptionIsRejectedWithoutChangingState(string descricao)
    {
        var ordem = Nova();
        Assert.Throws<ArgumentException>(() => ordem.RegistrarEtapa(TipoEtapa.Reparo, descricao, "mec-1", T0));
        Assert.Equal(EstadoExecucao.Fila, ordem.Estado);
        Assert.Empty(ordem.Etapas);
    }

    [Fact]
    public void DescriptionLengthBoundaryAndLoneSurrogate()
    {
        var ordem = Nova();
        ordem.RegistrarEtapa(TipoEtapa.Reparo, new string('x', 2000), "mec-1", T0);
        Assert.Throws<ArgumentException>(() => ordem.RegistrarEtapa(TipoEtapa.Reparo, new string('x', 2001), "mec-1", T0));
        Assert.Throws<ArgumentException>(() => ordem.RegistrarEtapa(TipoEtapa.Reparo, "ok\ud800", "mec-1", T0));
        Assert.Throws<ArgumentException>(() => ordem.RegistrarEtapa((TipoEtapa)9, "ok", "mec-1", T0));
        Assert.Single(ordem.Etapas);
    }

    [Fact]
    public void CompletionRequiresInProgressAndIsTerminal()
    {
        var ordem = Nova();
        Assert.Throws<TransicaoInvalidaException>(() => ordem.Concluir(T0));
        ordem.RegistrarEtapa(TipoEtapa.Reparo, "Reparo", "mec-1", T0);
        ordem.Concluir(T0.AddHours(1));
        Assert.Equal((EstadoExecucao.Concluida, (DateTimeOffset?)T0.AddHours(1)), (ordem.Estado, ordem.ConcluidaEm));
        Assert.Throws<TransicaoInvalidaException>(() => ordem.Concluir(T0));
        Assert.Throws<TransicaoInvalidaException>(() => ordem.RegistrarEtapa(TipoEtapa.Reparo, "Tarde", "mec-1", T0));
        Assert.Throws<TransicaoInvalidaException>(() => ordem.Falhar("Tarde", T0));
        Assert.Throws<TransicaoInvalidaException>(() => ordem.Reexecutar("Tarde", "sup", T0));
    }

    [Fact]
    public void FailureRequiresReasonKeepsStagesAndEnablesReexecutionOnlyOnce()
    {
        var ordem = Nova();
        ordem.RegistrarEtapa(TipoEtapa.Diagnostico, "Diagnóstico", "mec-1", T0);
        Assert.Throws<ArgumentException>(() => ordem.Falhar(" ", T0));
        Assert.Equal(EstadoExecucao.EmExecucao, ordem.Estado);
        Assert.Null(ordem.MotivoFalha);
        ordem.Falhar("Peça quebrada", T0.AddHours(1));
        Assert.Equal((EstadoExecucao.Falhou, true, "Peça quebrada"), (ordem.Estado, ordem.IntervencaoRequerida, ordem.MotivoFalha));
        Assert.Single(ordem.Etapas);
        Assert.Throws<ArgumentException>(() => ordem.Reexecutar("", "sup", T0));
        Assert.Throws<ArgumentException>(() => ordem.Reexecutar("Corrigido", "", T0));
        Assert.Equal(EstadoExecucao.Falhou, ordem.Estado);
        var registro = ordem.Reexecutar("Corrigido", "sup-1", T0.AddHours(2));
        Assert.Equal(("Corrigido", "sup-1"), (registro.Motivo, registro.Supervisor));
        Assert.Equal((EstadoExecucao.EmExecucao, false), (ordem.Estado, ordem.IntervencaoRequerida));
        Assert.Single(ordem.Etapas);
        Assert.Throws<TransicaoInvalidaException>(() => ordem.Reexecutar("Outra", "sup-1", T0));
    }

    [Fact]
    public void ReleasedEventParsingAcceptsOnlyCanonicalContract()
    {
        Guid evento = Guid.NewGuid(), os = Guid.NewGuid(), cliente = Guid.NewGuid(), veiculo = Guid.NewGuid();
        var json = $"{{\"eventId\":\"{evento}\",\"tipo\":\"ordem-servico.liberada.v1\",\"id\":\"{os}\",\"clienteId\":\"{cliente}\",\"veiculoId\":\"{veiculo}\"}}";
        Assert.True(EventoOsLiberada.TentarLer(json, out var lido));
        Assert.Equal(new EventoOsLiberada(evento, os, cliente, veiculo), lido);
        Assert.False(EventoOsLiberada.TentarLer(json.Replace(os.ToString(), os.ToString("N")), out _));
        Assert.False(EventoOsLiberada.TentarLer(json.Replace(os.ToString(), " " + os), out _));
        Assert.False(EventoOsLiberada.TentarLer(json.Replace("liberada", "criada"), out _));
        Assert.False(EventoOsLiberada.TentarLer(null, out _));
    }

    [Fact]
    public void CancellationFromQueueInProgressOrFailedAndKeepsStages()
    {
        var fila = Nova();
        fila.Cancelar("  Cliente desistiu ", T0.AddHours(1));
        Assert.Equal((EstadoExecucao.Cancelada, "Cliente desistiu", (DateTimeOffset?)T0.AddHours(1), T0.AddHours(1), 1), (fila.Estado, fila.MotivoCancelamento, fila.CanceladaEm, fila.AtualizadaEm, fila.Revisao));

        var emExecucao = Nova();
        emExecucao.RegistrarEtapa(TipoEtapa.Reparo, "Reparo", "mec-1", T0);
        emExecucao.Cancelar("Cliente desistiu", T0.AddHours(1));
        Assert.Equal((EstadoExecucao.Cancelada, 1, 2), (emExecucao.Estado, emExecucao.Etapas.Count, emExecucao.Revisao));

        Assert.Throws<TransicaoInvalidaException>(() => fila.Cancelar("Outra vez", T0));
        Assert.Throws<TransicaoInvalidaException>(() => fila.RegistrarEtapa(TipoEtapa.Reparo, "Tarde", "mec-1", T0));
        Assert.Throws<TransicaoInvalidaException>(() => fila.Falhar("Tarde", T0));
        Assert.Throws<TransicaoInvalidaException>(() => fila.Concluir(T0));
        Assert.Throws<TransicaoInvalidaException>(() => fila.Reexecutar("Tarde", "sup", T0));

        var concluida = Nova();
        concluida.RegistrarEtapa(TipoEtapa.Reparo, "Reparo", "mec-1", T0);
        concluida.Concluir(T0);
        Assert.Throws<TransicaoInvalidaException>(() => concluida.Cancelar("Tarde", T0));
        var falhou = Nova();
        falhou.Falhar("Peça quebrada", T0);
        falhou.Cancelar("Cancelada após falha", T0.AddHours(1));
        Assert.Equal((EstadoExecucao.Cancelada, false, "Cancelada após falha"), (falhou.Estado, falhou.IntervencaoRequerida, falhou.MotivoCancelamento));
        var vazia = Nova();
        Assert.Throws<ArgumentException>(() => vazia.Cancelar(" ", T0));
        Assert.Equal((EstadoExecucao.Fila, (string?)null), (vazia.Estado, vazia.MotivoCancelamento));
    }

    [Fact]
    public void CancelCommandParsingAcceptsOnlyTheCanonicalContract()
    {
        Guid evento = Guid.NewGuid(), os = Guid.NewGuid(), correlacao = Guid.NewGuid();
        var json = $"{{\"eventId\":\"{evento}\",\"tipo\":\"execucao.cancelar.v1\",\"correlacaoId\":\"{correlacao}\",\"ordemServicoId\":\"{os}\",\"ocorreuEm\":\"2026-11-10T12:00:00Z\",\"motivo\":\"Cliente desistiu\"}}";
        Assert.True(EventoCancelarExecucao.TentarLer(json, out var lido));
        Assert.Equal(new EventoCancelarExecucao(evento, os, "Cliente desistiu", correlacao), lido);
        Assert.True(EventoCancelarExecucao.TentarLer(json.Replace($"\"correlacaoId\":\"{correlacao}\",", ""), out var semCorrelacao));
        Assert.Null(semCorrelacao!.CorrelacaoId);
        Assert.True(EventoCancelarExecucao.TentarLer(json.Replace($"\"{correlacao}\"", "null"), out var nula));
        Assert.Null(nula!.CorrelacaoId);
        Assert.False(EventoCancelarExecucao.TentarLer(json.Replace(correlacao.ToString(), correlacao.ToString("N")), out _));
        Assert.False(EventoCancelarExecucao.TentarLer(json.Replace(os.ToString(), os.ToString("N")), out _));
        Assert.False(EventoCancelarExecucao.TentarLer(json.Replace("Cliente desistiu", ""), out _));
        Assert.False(EventoCancelarExecucao.TentarLer(json.Replace("execucao.cancelar.v1", "ordem-servico.liberada.v1"), out _));
        Assert.False(EventoCancelarExecucao.TentarLer(null, out _));
        Assert.True(EventoOsLiberada.TentarLer($"{{\"eventId\":\"{evento}\",\"tipo\":\"ordem-servico.liberada.v1\",\"correlacaoId\":\"{correlacao}\",\"id\":\"{os}\",\"clienteId\":\"{Guid.NewGuid()}\",\"veiculoId\":\"{Guid.NewGuid()}\"}}", out var liberada));
        Assert.Equal(correlacao, liberada!.CorrelacaoId);
    }
}

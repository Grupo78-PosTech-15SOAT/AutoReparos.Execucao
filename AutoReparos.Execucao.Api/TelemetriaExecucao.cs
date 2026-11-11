using OpenTelemetry.Resources;

namespace AutoReparos.Execucao.Api;

public static class TelemetriaExecucao
{
    public static ResourceBuilder Configurar(ResourceBuilder recurso, string servico, string ambiente) =>
        recurso.AddService(servico)
            .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", ambiente)]);
}

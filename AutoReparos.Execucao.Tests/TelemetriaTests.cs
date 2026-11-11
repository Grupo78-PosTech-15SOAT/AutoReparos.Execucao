using AutoReparos.Execucao.Api;
using AutoReparos.Execucao.Tests.Suporte;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AutoReparos.Execucao.Tests;

[Collection("postgres")]
public sealed class TelemetriaTests(PostgresFixture postgres)
{
    [Fact]
    public void ResourceCarriesServiceNameAndDeploymentEnvironment()
    {
        var recurso = TelemetriaExecucao.Configurar(ResourceBuilder.CreateEmpty(), "execution-teste", "Staging").Build();
        Assert.Equal("execution-teste", recurso.Attributes.Single(a => a.Key == "service.name").Value);
        Assert.Equal("Staging", recurso.Attributes.Single(a => a.Key == "deployment.environment.name").Value);
    }

    [Fact]
    public async Task HostAppliesResourceToTracesMetricsAndLogs()
    {
        using var ambiente = await Ambiente.CriarAsync(postgres);
        void Verificar(BaseProvider provider)
        {
            var atributos = provider.GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);
            Assert.Equal("autoreparos-execution", atributos["service.name"]);
            Assert.Equal("Testing", atributos["deployment.environment.name"]);
        }
        Verificar(ambiente.Fabrica.Services.GetRequiredService<TracerProvider>());
        Verificar(ambiente.Fabrica.Services.GetRequiredService<MeterProvider>());
        Verificar(ambiente.Fabrica.Services.GetRequiredService<OpenTelemetry.Logs.LoggerProvider>());
    }
}

using Amazon.Runtime.SharedInterfaces;

namespace AutoReparos.Execucao.Tests;

/// <summary>
/// No EKS o pod recebe AWS_ROLE_ARN e AWS_WEB_IDENTITY_TOKEN_FILE (IRSA) e o AWSSDK.Core troca o token pela
/// credencial chamando o STS. O Core não referencia o cliente do STS: carrega por reflexão o tipo
/// Amazon.SecurityToken.AmazonSecurityTokenServiceClient do assembly AWSSDK.SecurityToken e, sem ele no publish,
/// todo SQS e DynamoDB falha com "Assembly AWSSDK.SecurityToken could not be found or loaded".
/// </summary>
public sealed class CredenciaisIrsaTests
{
    [Fact]
    public void ClienteDoStsUsadoPeloIrsaEstaDisponivelNoRuntime()
    {
        var cliente = Type.GetType("Amazon.SecurityToken.AmazonSecurityTokenServiceClient, AWSSDK.SecurityToken");

        Assert.NotNull(cliente);
        Assert.True(typeof(ICoreAmazonSTS).IsAssignableFrom(cliente));
    }
}

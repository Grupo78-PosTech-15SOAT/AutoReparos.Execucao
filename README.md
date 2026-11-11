# AutoReparos Execução

Serviço autônomo da Fase 4 que conduz as ordens de serviço liberadas: fila de execução, etapas de diagnóstico e reparo, conclusão, falha com intervenção, reexecução manual e cancelamento comandado pela Saga do OS Service. Ele tem banco PostgreSQL próprio (usuário e database exclusivos) e não lê o banco do OS Service nem do Cobrança; toda integração acontece por eventos no AWS SQS.

## Arquitetura

| Projeto | Responsabilidade |
| --- | --- |
| `AutoReparos.Execucao.Domain` | `OrdemExecucao`, `EtapaExecucao`, `ReexecucaoExecucao`, outbox (`MensagemSaida`) e eventos processados. Regras de transição no agregado. |
| `AutoReparos.Execucao.Application` | `ExecucaoService`, contratos, leitura estrita de JSON (`LeitorJson`) e o contrato do evento de entrada. |
| `AutoReparos.Execucao.Infrastructure` | `ExecucaoDbContext` (EF Core + Npgsql), migrações escritas à mão, consumidor SQS, despachante do outbox, publicador SQS e `DependencyRetry`. |
| `AutoReparos.Execucao.Api` | Minimal API, autenticação JWT, telemetria OTLP e os serviços em segundo plano. |
| `AutoReparos.Execucao.Tests` / `.IntegrationTests` | Provas de comportamento com PostgreSQL real (Testcontainers) e SQS falso. |

A concorrência é otimista por `xmin` (coluna de sistema do PostgreSQL). Toda mudança no agregado incrementa `Revisao`, então até o registro de uma etapa (inserção filha) altera a linha pai. Dois pedidos simultâneos sobre a mesma execução resultam em um sucesso e um `409` com `codigo: conflito_concorrencia`.

## Configuração

Informe por variáveis de ambiente ou gerenciador de segredos. Nunca versione tokens ou strings de conexão.

| Chave | Finalidade |
| --- | --- |
| `ConnectionStrings__Execucao` | Conexão com o banco PostgreSQL do Execução |
| `Authentication__Authority` / `Authentication__Audience` | Emissor e audiência do JWT |
| `Authentication__RequireHttpsMetadata` | `false` apenas em ambiente local; o padrão é `true` |
| `AWS_REGION` | Região do SQS |
| `Sqs__LiberadasQueueUrl` | Fila de entrada com `ordem-servico.liberada.v1` e `execucao.cancelar.v1` |
| `Sqs__LiberadasDeadLetterQueueUrl` | DLQ da fila de entrada |
| `Sqs__OsServiceQueueUrl` | Fila de saída consumida pelo OS Service (conclusão, falha e respostas da Saga) |
| `Sqs__BillingQueueUrl` | Fila de saída consumida pelo Cobrança |
| `Sqs__DeadLetterQueueUrl` | DLQ de saída, comum aos dois destinos |
| `Telemetry__OtlpEndpoint` | Coletor OTLP (padrão `http://localhost:4317`) |
| `Telemetry__ServiceName` | Valor de `service.name` (padrão `autoreparos-execution`) |
| `BackgroundServices__Enabled` | `false` desliga o consumidor e o publicador (padrão `true`) |

A telemetria (traces, métricas e logs) exporta com `service.name` e `deployment.environment.name` (nome do ambiente ASP.NET Core). O serviço aplica as migrações na inicialização; provisione o banco e o papel do Execução sem acesso às tabelas de OS e Cobrança.

## Papéis e autenticação

- Fila e etapas (`GET /api/v1/fila-execucao`, `POST .../etapas`, `.../conclusao`, `.../falha`): `Mecanico` ou `Administrador`.
- Reexecução (`POST .../reexecucao`): somente `Administrador` (supervisor).
- A identidade de serviço `ServicoOS` não é usada: a entrada é feita pelo consumidor SQS, sem chamada HTTP do OS Service.
- Sem token: `401`. Papel inadequado: `403`. Também é `403` quando o token não traz a claim `sub`, porque o responsável pela etapa (ou o supervisor) vem dela.

## Rotas

| Rota | Corpo | Respostas |
| --- | --- | --- |
| `GET /api/v1/fila-execucao?status=&mecanico=` | - | `200`, `401`, `403`, `422` (`filtro_invalido`) |
| `POST /api/v1/execucoes/{id}/etapas` | `{ "tipo": "Diagnostico" \| "Reparo", "descricao": "..." }` | `201`, `401`, `403`, `404`, `409`, `422` (`etapa_invalida`) |
| `POST /api/v1/execucoes/{id}/reexecucao` | `{ "motivo": "..." }` | `202`, `401`, `403`, `404`, `409`, `422` (`reexecucao_invalida`) |
| `POST /api/v1/execucoes/{id}/conclusao` (extensão da Surface) | - | `200`, `401`, `403`, `404`, `409` |
| `POST /api/v1/execucoes/{id}/falha` (extensão da Surface) | `{ "motivo": "..." }` | `200`, `401`, `403`, `404`, `409`, `422` (`falha_invalida`) |

`conclusao` e `falha` não constam da tabela Surface do plano; foram adicionadas porque sem elas não há como concluir ou falhar uma execução (AC 3 e AC 4). Ambas exigem `Mecanico` ou `Administrador`.

Sem `status`, a fila devolve as execuções que ainda pedem ação (`Fila`, `EmExecucao` e `Falhou`); `status` aceita valores repetidos ou separados por vírgula (inclusive `Concluida` e `Cancelada`). `mecanico` filtra as execuções que têm etapa registrada por aquele responsável. Cada item traz as etapas em ordem de registro.

### Estados

`Fila` -> (primeira etapa) `EmExecucao` -> `Concluida`, ou `Falhou` (`intervencaoRequerida = true`). A reexecução só é aceita em `Falhou`: preserva todas as etapas, registra motivo, supervisor e horário e volta a execução para `EmExecucao` (ou `Fila` se não havia etapas). Etapas só entram em `Fila` ou `EmExecucao`; concluir exige `EmExecucao`.

`Cancelada` é um estado final, alcançado só pelo comando `execucao.cancelar.v1` (não há rota HTTP de cancelamento) a partir de `Fila`, `EmExecucao` ou `Falhou`; as etapas já registradas são preservadas, junto com o motivo (`motivoCancelamento`) e o horário (`canceladaEm`). Uma execução `Cancelada` sai da fila padrão e recusa etapas, conclusão, falha e reexecução com `409 execucao_estado_invalido`.

### Erros

Todos os erros de regra trazem `codigo` estável:

| Status | `codigo` | Quando |
| --- | --- | --- |
| 404 | `execucao_nao_encontrada` | Execução inexistente |
| 409 | `execucao_estado_invalido` | Transição não permitida pelo estado atual (inclui qualquer operação sobre execução `Cancelada`) |
| 409 | `conflito_concorrencia` | Outra operação alterou a execução primeiro (repita o pedido) |
| 422 | `etapa_invalida`, `reexecucao_invalida`, `falha_invalida`, `filtro_invalido` | Corpo ou filtro inválido; nada é persistido |
| 503 | `dependencia_indisponivel` | Banco inalcançável (com `Retry-After`) |

São `422` JSON quebrado, corpo que não é objeto, `Content-Type` diferente de JSON, tipos errados, `tipo` desconhecido, texto vazio, texto acima de 2000 caracteres, NUL (`\u0000`) e surrogate isolado.

## Eventos

### Entrada

Um consumidor SQS lê `Sqs__LiberadasQueueUrl` e despacha pelo campo `tipo`. Mensagem malformada (JSON inválido, `tipo` desconhecido, campo ausente ou inválido, GUID não canônico ou vazio) vai para a DLQ de entrada com o alerta `execucao.evento_invalido` e é removida da fila; nada é persistido nem respondido. Os GUIDs precisam estar no formato canônico 8-4-4-4-12 e as chaves são camelCase. O contrato completo da Saga está em `.specs/features/fase4/saga-mensageria/contracts.md`, no repositório principal.

#### `ordem-servico.liberada.v1`

```json
{
  "eventId": "6f1c2a3e-0b7d-4c55-9a1e-2d8f4b6c7a90",
  "tipo": "ordem-servico.liberada.v1",
  "correlacaoId": "c0a8012e-7d4b-4e21-9f33-0a1b2c3d4e5f",
  "id": "b3d5f7a1-1c2e-4f60-8a9b-0c1d2e3f4a5b",
  "clienteId": "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d",
  "veiculoId": "5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a8b9"
}
```

`id` é o identificador da OS. `correlacaoId` (a Saga) é opcional: ausente ou `null` vale nulo, e presente precisa ser um GUID válido; é persistido na execução e propagado às mensagens de saída. A OS entra na fila uma única vez: o `eventId` é gravado na tabela `eventos_processados` e `ordemServicoId` é único em `ordens_execucao`. Mensagens repetidas, inclusive concorrentes, não criam outra entrada nem etapa, não reiniciam uma execução em andamento e não alteram o `correlacaoId` já gravado.

Liberação **recusada por regra de negócio**: se a OS já tem execução `Cancelada` ou `Concluida`, ela não volta à fila e o serviço publica `execucao.liberacao-recusada.v1` (abaixo) para o OS Service, na mesma transação que registra o `eventId`. A reentrega do mesmo `eventId` não repete a resposta.

#### `execucao.cancelar.v1`

Comando da Saga (compensação da etapa 2), na mesma fila e no mesmo consumidor:

```json
{
  "eventId": "2b7e4c1a-9d3f-4a68-8b52-6e0f1c9a7d34",
  "tipo": "execucao.cancelar.v1",
  "correlacaoId": "c0a8012e-7d4b-4e21-9f33-0a1b2c3d4e5f",
  "ordemServicoId": "b3d5f7a1-1c2e-4f60-8a9b-0c1d2e3f4a5b",
  "ocorreuEm": "2026-11-10T12:00:00Z",
  "motivo": "Orçamento cancelado pelo cliente"
}
```

`eventId`, `ordemServicoId` e `motivo` (texto de até 2000 caracteres) são obrigatórios; `correlacaoId` é opcional. O efeito vale para a execução da OS e é decidido pelo estado:

| Estado atual | Resultado | Resposta ao OS Service |
| --- | --- | --- |
| `Fila`, `EmExecucao` ou `Falhou` | vai a `Cancelada`, preservando as etapas (em `Falhou`, a intervenção deixa de ser necessária e a reexecução passa a dar `409 execucao_estado_invalido`) | `execucao.cancelada.v1` |
| `Cancelada` (comando repetido com outro `eventId`) | nada muda; o primeiro motivo é mantido | `execucao.cancelada.v1` |
| `Concluida` | recusado | `execucao.cancelamento-recusado.v1` |
| inexistente | recusado | `execucao.cancelamento-recusado.v1` (sem `execucaoId`) |

A deduplicação é persistida por `eventId` na mesma tabela `eventos_processados`, e a resposta é gravada no outbox na mesma transação. Entrega repetida ou concorrente do mesmo `eventId` cancela uma vez e responde uma vez. Se outra operação alterar a execução durante o cancelamento (concorrência por `xmin`), o serviço refaz a leitura (até 3 tentativas) em vez de perder o comando.

### Saída: `execucao.concluida.v1`

Publicado quando a execução conclui, para o OS Service e para o Cobrança. Há **uma linha de outbox por consumidor**, gravada na mesma transação da conclusão, com o **mesmo `eventId`** e uma fila por destino (`Sqs__OsServiceQueueUrl` e `Sqs__BillingQueueUrl`); cada consumidor deduplica pelo `eventId` do seu lado.

```json
{
  "eventId": "9d2a1c44-5b3e-4f10-8e7a-1b2c3d4e5f60",
  "tipo": "execucao.concluida.v1",
  "correlacaoId": "<correlacaoId da liberação, ou null>",
  "ocorreuEm": "2026-11-13T18:45:00+00:00",
  "id": "<id da execução>",
  "ordemServicoId": "<id da OS>",
  "clienteId": "<id do cliente>",
  "veiculoId": "<id do veículo>",
  "concluidaEm": "2026-11-13T18:45:00+00:00"
}
```

### Saída: `execucao.falhou.v1`

Publicado só para o OS Service quando a execução falha: mesmos campos, mais `motivo` e `falhouEm` no lugar de `concluidaEm`. `correlacaoId` vem da liberação e é `null` quando ela não o trouxe; `correlacaoId` e `ocorreuEm` são acréscimos, os demais campos e nomes não mudaram.

### Saída: respostas da Saga (só para o OS Service)

As três mensagens abaixo usam o envelope comum e vão para `Sqs__OsServiceQueueUrl` pelo outbox, com o mesmo retry, DLQ de saída e alerta das demais. Cada uma tem `eventId` próprio. `correlacaoId` vem do comando (no cancelamento, da execução quando o comando não o traz) e é `null` se ausente; `execucaoId` só aparece quando a execução existe.

```json
{
  "eventId": "e1f0a2b3-4c5d-4e6f-8a7b-9c0d1e2f3a4b",
  "tipo": "execucao.cancelada.v1",
  "correlacaoId": "c0a8012e-7d4b-4e21-9f33-0a1b2c3d4e5f",
  "ordemServicoId": "b3d5f7a1-1c2e-4f60-8a9b-0c1d2e3f4a5b",
  "ocorreuEm": "2026-11-10T12:00:01+00:00",
  "execucaoId": "<id da execução>"
}
```

- `execucao.cancelada.v1`: campos acima.
- `execucao.cancelamento-recusado.v1`: os mesmos campos mais `motivo` (por exemplo `Execução já concluída não pode ser cancelada.`).
- `execucao.liberacao-recusada.v1`: sem `execucaoId`, com `motivo` (`Execução da OS já foi cancelada.` ou `Execução da OS já foi concluída.`).

### Entrega, retentativas e DLQ

A entrega é pelo menos uma vez; os consumidores devem deduplicar pelo `eventId`. O limite padrão por dependência é de 5 tentativas com backoff exponencial (100 ms, 200 ms, 400 ms, 800 ms, teto de 5 s) somado a jitter de 0 a 99 ms.

- **SQS (saída):** ao esgotar as 5 tentativas o evento é enviado à DLQ de saída e o alerta `execucao.sqs.exhausted` é emitido. Se a DLQ também falhar, o alerta `execucao.sqs.dlq_failed` é emitido e a linha continua pendente no outbox, com nova tentativa agendada.
- **Banco (consumidor):** falha transitória (erro de rede, timeout) repete até 5 vezes; ao esgotar, o evento vai à DLQ de entrada com o alerta `execucao.banco.exhausted`. Se o envio à DLQ falhar, a mensagem não é removida e volta à fila após o timeout de visibilidade (alerta `execucao.entrada.dlq_failed`). Erros que não são transitórios não são repetidos.
- **Banco (API):** falha transitória vira `503 dependencia_indisponivel` com `Retry-After: 5`.

Os alertas hoje são registros de log `Critical` (`LoggingAlertSink`); ligue-os ao sistema de alertas do ambiente de observabilidade.

## Como rodar localmente e as provas

As provas usam Docker (imagem `postgres:16-alpine` via Testcontainers) e um `IAmazonSQS` falso: não precisam de AWS nem de variáveis de ambiente.

```sh
dotnet restore AutoReparos.Execucao.slnx
dotnet build AutoReparos.Execucao.slnx -m:1
dotnet test AutoReparos.Execucao.Tests
dotnet test AutoReparos.Execucao.IntegrationTests
dotnet format AutoReparos.Execucao.slnx --verify-no-changes --no-restore
```

Filtros usados pelos checks da feature: `ReleasedOrderQueuedOnce` (C1), `ExecutionStageHistory` (C2), `CompletionPublishedToConsumers` (C3, projeto de integração), `ManualReexecutionPreservesStages` (C4), `DuplicateEventDeduplication` (C5) e `DependencyRetryExhaustion` (C6). Os testes da Saga (cancelamento, liberação recusada e correlação) ficam em `CancelamentoExecucaoTests`, com os filtros `ExecutionCancellation`, `ReleaseRefusal` e `SagaCorrelation`.

Para publicar a imagem: `docker build -t autoreparos-execution .` neste diretório.

## CI/CD e configuração do repositório

O pipeline `.github/workflows/ci-cd.yml` roda em pull request para `main` e em push na `develop` e na `main`, com os jobs `build` (compila e confere `dotnet format`), `test` (testes de `AutoReparos.Execucao.Tests` e `AutoReparos.Execucao.IntegrationTests`, cobertura mínima de linhas de 80%) e `quality` (SonarCloud).

Para o job `quality` passar, o repositório precisa destes itens em **Settings → Secrets and variables → Actions**:

| Nome | Onde criar | Obrigatório | Valor |
| --- | --- | --- | --- |
| `SONAR_TOKEN` | aba **Secrets** (do repositório ou da organização, com acesso a este repositório) | sim: sem ele o job `quality` falha de propósito | token gerado no SonarCloud em *My Account → Security*, por quem pode analisar os projetos da organização |
| `SONAR_ORGANIZATION` | aba **Variables** | não | padrão `grupo78-postech-15soat` |
| `SONAR_PROJECT_KEY` | aba **Variables** | não | padrão `Grupo78-PosTech-15SOAT_AutoReparos.Execucao` |

O projeto `Grupo78-PosTech-15SOAT_AutoReparos.Execucao` precisa existir no SonarCloud (ou o token precisa poder criá-lo). Nunca versione o token. Depois de criar o secret, reexecute o job `quality` da última execução; não é preciso novo commit.

Quando a proteção da `main` for aplicada (AD-026 do repositório pai), os jobs `build`, `test` e `quality` serão os status checks obrigatórios.

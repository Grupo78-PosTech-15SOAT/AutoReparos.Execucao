# Filas da Execução (a fila pertence ao consumidor).
# - liberadas: comandos da Saga enviados pelo OS (Sqs:LiberadasQueueUrl), com DLQ (Sqs:LiberadasDeadLetterQueueUrl).
# - saida-dlq: eventos do outbox da Execução esgotados (Sqs:DeadLetterQueueUrl), lidos pelo reprocessador da DLQ.
resource "aws_sqs_queue" "liberadas_dlq" {
  name                      = "autoreparos-execucao-liberadas-dlq"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
}

resource "aws_sqs_queue" "liberadas" {
  name                       = "autoreparos-execucao-liberadas"
  visibility_timeout_seconds = 90
  message_retention_seconds  = 345600
  receive_wait_time_seconds  = 20
  sqs_managed_sse_enabled    = true
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.liberadas_dlq.arn
    maxReceiveCount     = 5
  })
}

resource "aws_sqs_queue" "saida_dlq" {
  name                      = "autoreparos-execucao-saida-dlq"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
}

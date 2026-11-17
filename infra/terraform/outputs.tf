output "ecr_url" {
  description = "URL do repositório ECR do serviço (a tag da imagem é o SHA do commit)."
  value       = aws_ecr_repository.servico.repository_url
}

output "irsa_role_arn" {
  description = "ARN da role IRSA anotada no ServiceAccount do chart."
  value       = aws_iam_role.irsa.arn
}

output "segredo_database_arn" {
  description = "ARN do segredo com a connection string do serviço."
  value       = aws_secretsmanager_secret.database.arn
}

output "fila_liberadas_url" {
  description = "URL da fila de entrada da Execução."
  value       = aws_sqs_queue.liberadas.url
}

output "fila_liberadas_dlq_url" {
  description = "URL da DLQ da fila de entrada."
  value       = aws_sqs_queue.liberadas_dlq.url
}

output "fila_saida_dlq_url" {
  description = "URL da DLQ dos eventos publicados pela Execução."
  value       = aws_sqs_queue.saida_dlq.url
}

output "topico_eventos_arn" {
  description = "ARN do tópico SNS dos eventos da Execução (Sns__EventosTopicArn)."
  value       = aws_sns_topic.eventos.arn
}

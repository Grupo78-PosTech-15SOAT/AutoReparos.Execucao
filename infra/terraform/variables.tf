variable "regiao" {
  description = "Região AWS dos recursos do serviço."
  type        = string
  default     = "us-east-1"
}

variable "ambiente" {
  description = "Ambiente da plataforma; define o prefixo dos parâmetros SSM e do segredo."
  type        = string
  default     = "production"
}

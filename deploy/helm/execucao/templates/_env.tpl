{{/* Configuração específica da Execução. */}}
{{- define "servico.env" -}}
- name: Authentication__Authority
  value: {{ .Values.autenticacao.emissor | quote }}
{{- with .Values.autenticacao.audiencia }}
- name: Authentication__Audience
  value: {{ . | quote }}
{{- end }}
- name: Telemetry__ServiceName
  value: {{ .Values.telemetria.nomeServico | quote }}
- name: Telemetry__OtlpEndpoint
  value: {{ .Values.telemetria.otlpEndpoint | quote }}
- name: Sqs__LiberadasQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.liberadas) | quote }}
- name: Sqs__LiberadasDeadLetterQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.liberadasDlq) | quote }}
- name: Sqs__OsServiceQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.saga) | quote }}
- name: Sqs__BillingQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.cobranca) | quote }}
- name: Sqs__DeadLetterQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.saidaDlq) | quote }}
- name: Sns__EventosTopicArn
  value: {{ include "servico.topico" (list . .Values.topicos.eventos) | quote }}
{{- end }}

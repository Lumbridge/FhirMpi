{{- define "openmpi.name" -}}
openmpi
{{- end }}

{{- define "openmpi.labels" -}}
app.kubernetes.io/name: {{ include "openmpi.name" . }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "fhir-mpi.name" -}}
fhir-mpi
{{- end }}

{{- define "fhir-mpi.labels" -}}
app.kubernetes.io/name: {{ include "fhir-mpi.name" . }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

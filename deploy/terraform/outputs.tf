output "fhir_store_name" {
  value = google_healthcare_fhir_store.registry.id
}

output "gke_cluster_name" {
  value = google_container_cluster.registry.name
}

output "workload_service_account" {
  value = google_service_account.workload.email
}

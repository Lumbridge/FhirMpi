locals {
  services = toset([
    "container.googleapis.com",
    "healthcare.googleapis.com",
    "iamcredentials.googleapis.com",
    "monitoring.googleapis.com",
    "logging.googleapis.com"
  ])
}

resource "google_project_service" "required" {
  for_each           = local.services
  project            = var.project_id
  service            = each.value
  disable_on_destroy = false
}

resource "google_healthcare_dataset" "registry" {
  name       = replace(var.name, "-", "_")
  location   = var.region
  depends_on = [google_project_service.required]
}

resource "google_healthcare_fhir_store" "registry" {
  name    = "${replace(var.name, "-", "_")}_r4"
  dataset = google_healthcare_dataset.registry.id
  version = "R4"

  enable_update_create           = true
  disable_referential_integrity  = false
  disable_resource_versioning    = false
  default_search_handling_strict = true

  complex_data_type_reference_parsing = "ENABLED"
}

resource "google_service_account" "workload" {
  account_id   = substr(replace("${var.name}-workload", "_", "-"), 0, 30)
  display_name = "UnifyEMPI Workload Identity"
}

resource "google_project_iam_member" "fhir_editor" {
  project = var.project_id
  role    = "roles/healthcare.fhirResourceEditor"
  member  = "serviceAccount:${google_service_account.workload.email}"
}

resource "google_container_cluster" "registry" {
  name       = var.name
  location   = var.region
  network    = var.gke_network
  subnetwork = var.gke_subnetwork

  deletion_protection      = true
  remove_default_node_pool = true
  initial_node_count       = 1
  networking_mode          = "VPC_NATIVE"
  enable_shielded_nodes    = true

  workload_identity_config {
    workload_pool = "${var.project_id}.svc.id.goog"
  }

  private_cluster_config {
    enable_private_nodes    = true
    enable_private_endpoint = false
    master_ipv4_cidr_block  = "172.16.0.0/28"
  }

  release_channel {
    channel = "REGULAR"
  }

  depends_on = [google_project_service.required]
}

resource "google_container_node_pool" "registry" {
  name     = "${var.name}-pool"
  location = var.region
  cluster  = google_container_cluster.registry.name

  autoscaling {
    min_node_count = 2
    max_node_count = 10
  }

  node_config {
    machine_type    = "e2-standard-4"
    service_account = google_service_account.workload.email
    oauth_scopes    = ["https://www.googleapis.com/auth/cloud-platform"]

    workload_metadata_config {
      mode = "GKE_METADATA"
    }

    shielded_instance_config {
      enable_secure_boot          = true
      enable_integrity_monitoring = true
    }
  }
}

resource "google_service_account_iam_member" "workload_identity" {
  service_account_id = google_service_account.workload.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "serviceAccount:${var.project_id}.svc.id.goog[${var.kubernetes_namespace}/unifyempi]"
}

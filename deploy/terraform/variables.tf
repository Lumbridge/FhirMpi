variable "project_id" {
  description = "GCP project for the isolated OpenMPI deployment."
  type        = string
}

variable "region" {
  description = "GCP region shared by GKE and Healthcare API."
  type        = string
  default     = "europe-west2"
}

variable "name" {
  description = "Resource name prefix."
  type        = string
  default     = "openmpi"
}

variable "kubernetes_namespace" {
  description = "Kubernetes namespace containing the OpenMPI service account."
  type        = string
  default     = "openmpi"
}

variable "gke_subnetwork" {
  description = "Existing subnetwork self-link for the private GKE cluster."
  type        = string
}

variable "gke_network" {
  description = "Existing VPC network self-link for the private GKE cluster."
  type        = string
}

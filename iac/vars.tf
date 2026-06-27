variable "env_id" {
  type        = string
  description = "The environment id"
  default     = "prod"
}

variable "src_key" {
  type        = string
  description = "The infrastructure source"
  default     = "terraform"
}

variable "subscription_id" {
  type        = string
  description = "The Azure subscription id"
}

variable "connection_string_translation" {
  type        = string
  description = "Npgsql connection string for the TMS (translation) database"
  sensitive   = true
}

variable "connection_string_auth" {
  type        = string
  description = "Npgsql connection string for the Auth database"
  sensitive   = true
}

variable "openiddict_signing_key_rsa_private_key_xml" {
  type        = string
  description = "Base64 of RSA.ToXmlString(true) for the OpenIddict signing key"
  sensitive   = true
}

variable "openiddict_encryption_key" {
  type        = string
  description = "Base64 of a 32-byte OpenIddict encryption key"
  sensitive   = true
}

variable "openiddict_api_client_secret" {
  type        = string
  description = "OpenIddict API client secret shared with the service client"
  sensitive   = true
}

variable "smtp_username" {
  type        = string
  description = "Brevo SMTP username"
  sensitive   = true
}

variable "smtp_password" {
  type        = string
  description = "Brevo SMTP key used as the SMTP password"
  sensitive   = true
}

variable "smtp_sender_email" {
  type        = string
  description = "Verified sender email address"
}

variable "admin_username" {
  type        = string
  description = "Seeded admin username"
}

variable "admin_email" {
  type        = string
  description = "Seeded admin email"
}

variable "admin_password" {
  type        = string
  description = "Seeded admin password"
  sensitive   = true
}

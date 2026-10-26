variable "region" {
  description = "AWS Region for every resource of the course."
  type        = string
  default     = "us-east-1"
}

variable "project" {
  description = "Prefix for resource names."
  type        = string
  default     = "hicleanarch"
}

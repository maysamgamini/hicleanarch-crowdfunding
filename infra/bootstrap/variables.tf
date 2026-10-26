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

variable "github_repository" {
  description = "The repository whose main-branch workflows may deploy, as owner/name."
  type        = string
  default     = "maysamgamini/hicleanarch-crowdfunding"
}

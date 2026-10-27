# CloudFront in front of every request, so clients send their tokens only over HTTPS: its default
# certificate needs no domain name. The totals reply is cached for at most 2 seconds, as the
# origin's Cache-Control says (lesson s3-a02); everything else passes through uncached. The load
# balancer accepts only CloudFront's addresses, and only requests carrying the origin header below.
# Destroy takes several minutes: CloudFront disables the distribution before it can delete it.

resource "aws_cloudfront_cache_policy" "totals" {
  name        = "${var.project}-totals"
  comment     = "Honour the origin's max-age, never more than 2 seconds"
  min_ttl     = 0
  default_ttl = 0
  max_ttl     = 2

  parameters_in_cache_key_and_forwarded_to_origin {
    enable_accept_encoding_gzip   = true
    enable_accept_encoding_brotli = true
    cookies_config {
      cookie_behavior = "none"
    }
    headers_config {
      header_behavior = "none"
    }
    query_strings_config {
      query_string_behavior = "none"
    }
  }
}

data "aws_cloudfront_cache_policy" "disabled" {
  name = "Managed-CachingDisabled"
}

# What reaches the origin: every viewer header, Authorization included, and CloudFront's own header
# with the viewer's address, which the gateway's per-client limits read (lesson s3-l11). Viewer
# headers alone would not include it. The load balancer routes without host rules, so the forwarded
# Host does not matter.
resource "aws_cloudfront_origin_request_policy" "viewer_and_address" {
  name    = "${var.project}-viewer-and-address"
  comment = "All viewer headers, with CloudFront-Viewer-Address"

  cookies_config {
    cookie_behavior = "all"
  }
  headers_config {
    header_behavior = "allViewerAndWhitelistCloudFront"
    headers {
      items = ["CloudFront-Viewer-Address"]
    }
  }
  query_strings_config {
    query_string_behavior = "all"
  }
}

resource "aws_cloudfront_origin_request_policy" "viewer_address" {
  name    = "${var.project}-viewer-address"
  comment = "CloudFront-Viewer-Address only, for the cached totals"

  cookies_config {
    cookie_behavior = "none"
  }
  headers_config {
    header_behavior = "whitelist"
    headers {
      items = ["CloudFront-Viewer-Address"]
    }
  }
  query_strings_config {
    query_string_behavior = "none"
  }
}

resource "aws_cloudfront_distribution" "public" {
  enabled     = true
  comment     = "${var.project} public reads"
  price_class = "PriceClass_100"

  origin {
    origin_id   = "api"
    domain_name = aws_lb.api.dns_name

    # The hop from CloudFront to the load balancer is still plain HTTP: without a domain name the load
    # balancer has no certificate. Production gives it one from ACM and sets https-only here.
    custom_origin_config {
      http_port              = 80
      https_port             = 443
      origin_protocol_policy = "http-only"
      origin_ssl_protocols   = ["TLSv1.2"]
    }

    custom_header {
      name  = local.origin_header
      value = random_password.origin_header.result
    }
  }

  # Every other request, signed-in calls and changes included: passed through, never cached here.
  default_cache_behavior {
    target_origin_id         = "api"
    viewer_protocol_policy   = "redirect-to-https"
    allowed_methods          = ["GET", "HEAD", "OPTIONS", "PUT", "POST", "PATCH", "DELETE"]
    cached_methods           = ["GET", "HEAD"]
    cache_policy_id          = data.aws_cloudfront_cache_policy.disabled.id
    origin_request_policy_id = aws_cloudfront_origin_request_policy.viewer_and_address.id
  }

  # The totals reply: cached by URL alone. When it expires, concurrent requests at one edge
  # location are collapsed into one request to the origin.
  ordered_cache_behavior {
    path_pattern             = "/v1/campaigns/*/totals"
    target_origin_id         = "api"
    viewer_protocol_policy   = "redirect-to-https"
    allowed_methods          = ["GET", "HEAD"]
    cached_methods           = ["GET", "HEAD"]
    cache_policy_id          = aws_cloudfront_cache_policy.totals.id
    origin_request_policy_id = aws_cloudfront_origin_request_policy.viewer_address.id
    compress                 = true
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    cloudfront_default_certificate = true
  }
}

# CloudFront adds this header to every request it sends on; the load balancer refuses a request
# without it, so another distribution cannot reach the origin through CloudFront's addresses.
locals {
  origin_header = "X-Origin-Verify"
}

resource "random_password" "origin_header" {
  length  = 32
  special = false
}

output "public_reads_url" {
  description = "CloudFront's address for public reads such as /v1/campaigns/{id}/totals."
  value       = "https://${aws_cloudfront_distribution.public.domain_name}"
}

# CloudFront in front of the public reads. The totals reply is cached for at most 2 seconds, as
# the origin's Cache-Control says (lesson s3-a02); everything else passes through uncached.
# Signed-in calls and every change go to the load balancer directly, not through CloudFront.
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

data "aws_cloudfront_origin_request_policy" "all_viewer_except_host" {
  name = "Managed-AllViewerExceptHostHeader"
}

resource "aws_cloudfront_distribution" "public" {
  enabled     = true
  comment     = "${var.project} public reads"
  price_class = "PriceClass_100"

  origin {
    origin_id   = "api"
    domain_name = aws_lb.api.dns_name

    custom_origin_config {
      http_port              = 80
      https_port             = 443
      origin_protocol_policy = "http-only" # the sandbox load balancer has no certificate
      origin_ssl_protocols   = ["TLSv1.2"]
    }
  }

  # Campaign pages and their reads: passed through, never cached here.
  default_cache_behavior {
    target_origin_id         = "api"
    viewer_protocol_policy   = "redirect-to-https"
    allowed_methods          = ["GET", "HEAD", "OPTIONS"]
    cached_methods           = ["GET", "HEAD"]
    cache_policy_id          = data.aws_cloudfront_cache_policy.disabled.id
    origin_request_policy_id = data.aws_cloudfront_origin_request_policy.all_viewer_except_host.id
  }

  # The totals reply: cached by URL alone. When it expires, concurrent requests at one edge
  # location are collapsed into one request to the origin.
  ordered_cache_behavior {
    path_pattern           = "/v1/campaigns/*/totals"
    target_origin_id       = "api"
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    cache_policy_id        = aws_cloudfront_cache_policy.totals.id
    compress               = true
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

output "public_reads_url" {
  description = "CloudFront's address for public reads such as /v1/campaigns/{id}/totals."
  value       = "https://${aws_cloudfront_distribution.public.domain_name}"
}

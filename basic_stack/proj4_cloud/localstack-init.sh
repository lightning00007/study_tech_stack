#!/bin/bash
# =============================================================================
# localstack-init.sh — Pre-create AWS resources in LocalStack on startup
# =============================================================================
# This script runs automatically when LocalStack starts.
# It creates the SNS topic that the application expects.
# Without this, the first publish attempt would fail with "topic not found".
#
# In a real AWS environment, these resources are created via:
#   - AWS CLI / Terraform / CDK (infrastructure as code)
#   - CloudFormation templates
# Here, we script it locally for development convenience.
# =============================================================================

echo "Creating SNS topic: book-library-events..."
aws --endpoint-url=http://localhost:4566 sns create-topic \
    --name book-library-events \
    --region us-east-1

echo "LocalStack resources created successfully."

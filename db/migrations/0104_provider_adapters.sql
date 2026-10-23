-- Install with the application release that implements these exact mappings. No credential,
-- model capability, account attestation, token price or enabled state is supplied here.
BEGIN;
INSERT INTO assistant.confirmed_mappings VALUES
 ('openai','openai_responses_v1','2026-10-23',ARRAY[
  'https://developers.openai.com/api/reference/resources/responses/streaming-events',
  'https://developers.openai.com/api/docs/guides/function-calling',
  'https://developers.openai.com/api/docs/guides/reasoning',
  'https://developers.openai.com/api/docs/models/gpt-6.1-sol',
  'https://developers.openai.com/api/docs/guides/prompt-caching']),
 ('xai','xai_responses_v1','2026-10-23',ARRAY[
  'https://docs.x.ai/developers/model-capabilities/text/best-practices',
  'https://docs.x.ai/developers/model-capabilities/text/reasoning',
  'https://docs.x.ai/developers/advanced-api-usage/prompt-caching/usage-and-pricing']),
 ('google','generate_content_v1','2026-10-23',ARRAY[
  'https://ai.google.dev/api/generate-content',
  'https://ai.google.dev/gemini-api/docs/generate-content/thought-signatures',
  'https://ai.google.dev/gemini-api/docs/thinking']),
 ('meta','meta_responses_v1','2026-10-23',ARRAY[
  'https://dev.meta.ai/docs/protocols/responses',
  'https://dev.meta.ai/docs/token-counting',
  'https://dev.meta.ai/docs/prompt-caching',
  'https://dev.meta.ai/docs/reasoning']);
INSERT INTO assistant.mapping_efforts VALUES
 ('openai','openai_responses_v1',ARRAY['low','medium','high','xhigh','max']),
 ('xai','xai_responses_v1',ARRAY['low','medium','high','xhigh']),
 ('google','generate_content_v1',ARRAY['minimal','low','medium','high']),
 ('meta','meta_responses_v1',ARRAY['minimal','low','medium','high','xhigh','max']);
INSERT INTO assistant.adapter_mappings VALUES
 ('openai','openai_responses_v1'),('xai','xai_responses_v1'),('google','generate_content_v1'),('meta','meta_responses_v1');
COMMIT;

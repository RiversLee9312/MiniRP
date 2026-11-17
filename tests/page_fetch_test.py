import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from page_fetch import fetch_document


class PageFetchTest(unittest.TestCase):
    def test_api_success_never_starts_local_and_has_no_harness_cutoff(self):
        body = "API body" * 30000
        with patch("page_fetch.real_web.fetch_page_details", return_value=(body, "API title")) as api, \
             patch("fetch_agent.FetchAgent.fetch") as local:
            result = fetch_document("https://example.com", backend="api_then_local")
        api.assert_called_once_with("https://example.com", None)
        local.assert_not_called()
        self.assertEqual(result["content"], body)
        self.assertIsNone(result["truncated"])

    def test_api_failure_falls_back_and_explicit_local_skips_api(self):
        local_result = {"url": "https://example.com", "title": "local", "content": "完整本地正文", "backend": "local", "complete": True, "truncated": False}
        with patch("page_fetch.real_web.fetch_page_details", return_value=("抓取失败：超时", "")) as api, \
             patch("fetch_agent.FetchAgent.fetch", return_value=local_result.copy()) as local:
            result = fetch_document("https://example.com", backend="api_then_local")
            self.assertEqual(result["attempts"][0]["backend"], "api")
            self.assertEqual(result["backend"], "local")
            api.reset_mock()
            fetch_document("https://example.com", backend="local")
            api.assert_not_called()
            self.assertEqual(local.call_count, 2)

    def test_pure_api_does_not_fallback_and_empty_body_is_failure(self):
        with patch("page_fetch.real_web.fetch_page_details", return_value=("", "empty")), patch("fetch_agent.FetchAgent.fetch") as local:
            self.assertIn("error", fetch_document("https://example.com", backend="api"))
            local.assert_not_called()


if __name__ == "__main__":
    unittest.main()

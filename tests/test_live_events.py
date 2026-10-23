"""The live-check SSE reader distinguishes heartbeat comments from updates."""
import io
import queue
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from check_live_calculation import Events


class LiveEventTests(unittest.TestCase):
    def read(self, payload):
        stream = Events.__new__(Events)
        stream.response = io.BytesIO(payload)
        stream.events = queue.Queue()
        stream.read()
        return stream

    def test_heartbeat_before_update_does_not_replace_update(self):
        stream = self.read(b': ping\n\nevent: context\ndata: {"generation":2}\n\n')
        self.assertEqual(stream.next(.01), {'event': 'context', 'data': {'generation': 2}})
        with self.assertRaises(queue.Empty):
            stream.next(.01)

    def test_comment_inside_event_preserves_payload(self):
        stream = self.read(b'event: context\n: ping\ndata: {"generation":3}\n\n')
        self.assertEqual(stream.next(.01), {'event': 'context', 'data': {'generation': 3}})

    def test_heartbeat_only_stream_has_no_business_events(self):
        stream = self.read(b': ping\n\n: ping\r\n\r\n')
        with self.assertRaises(queue.Empty):
            stream.next(.01)


if __name__ == '__main__':
    unittest.main()

import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('driver', Path(__file__).with_name('verify-library-state.py'))
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


class ManualContractTests(unittest.TestCase):
    def test_successful_restore_can_omit_its_null_result(self):
        api = driver.Api()
        responses = iter([{'Id': 'restore-1'}, {'State': 'Completed'}])
        api.call = lambda *args, **kwargs: next(responses)
        self.assertIsNone(api.manual('/JellyfinCompressor/Originals/original-1/Restore'))

    def test_manual_call_waits_for_completion_and_returns_the_result(self):
        api = driver.Api()
        calls = []
        responses = iter([
            {'Id': 'operation-1', 'State': 'Running'},
            {'Id': 'operation-1', 'State': 'Running'},
            {'Id': 'operation-1', 'State': 'Completed', 'Result': [{'Eligible': True}]},
        ])

        def call(path, method='GET', **kwargs):
            calls.append((path, method))
            return next(responses)

        api.call = call
        with patch.object(driver.time, 'sleep'):
            self.assertEqual(api.manual('/JellyfinCompressor/Analyze'), [{'Eligible': True}])
        self.assertEqual(calls, [('/JellyfinCompressor/Analyze', 'POST'),
                                 ('/JellyfinCompressor/Manual/operation-1', 'GET'),
                                 ('/JellyfinCompressor/Manual/operation-1', 'GET')])

    def test_failed_manual_operation_stops_the_fixture_instead_of_testing_stale_data(self):
        api = driver.Api()
        responses = iter([{'Id': 'operation-1'}, {'State': 'Failed', 'Error': 'source changed'}])
        api.call = lambda *args, **kwargs: next(responses)
        with self.assertRaisesRegex(RuntimeError, 'source changed'):
            api.manual('/JellyfinCompressor/Analyze')


if __name__ == '__main__':
    unittest.main()

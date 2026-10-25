"""Regression fixtures for the world acceptance log reader; no game/account required."""
import unittest
from world_log_evidence import world_events, overworld_regions


class WorldLogEvidenceTests(unittest.TestCase):
    """Exercise both upstream log formats and ensure stacktraces are not lifecycle events."""

    def test_legacy_and_26_world_layout(self):
        paths = ['region/r.0.0.mca', 'dimensions/minecraft/overworld/region/r.0.0.mca',
                 'dimensions/minecraft/overworld/entities/r.0.0.mca', 'DIM-1/region/r.0.0.mca']
        self.assertEqual(overworld_regions(paths), paths[:2])

    def test_xml_lifecycle_and_error(self):
        messages = [
            ('INFO', 'Starting integrated minecraft server version 1.21.1'),
            ('INFO', 'Stopping server'),
            ('ERROR', 'Stop: Invalid name parameter.'),
        ]
        text = '\n'.join(
            '<log4j:Event level="' + level + '"><log4j:Message><![CDATA['
            + message + ']]></log4j:Message></log4j:Event>'
            for level, message in messages
        )
        self.assertEqual(world_events(text), dict(
            integratedServerStarts=1, integratedServerStops=1, errorEvents=1))

    def test_neoforge_lifecycle_and_shutdown_error(self):
        text = (
            '[22:24:20] [Server thread/INFO] [minecraft/IntegratedServer]: Starting integrated minecraft server version 1.21.1\n'
            '[22:25:30] [Sound engine/ERROR] [mojang/OpenAlUtil]: Stop: Invalid name parameter.\n'
            '[22:25:30] [Server thread/INFO] [minecraft/MinecraftServer]: Stopping server\n'
            'java.lang.Exception: Stopping server\n'
        )
        self.assertEqual(world_events(text), dict(
            integratedServerStarts=1, integratedServerStops=1, errorEvents=1))

    def test_plain_without_logger_and_non_events(self):
        text = '[12:00:01] [Server thread/INFO]: Stopping server\nERROR words alone are not events\n'
        self.assertEqual(world_events(text), dict(
            integratedServerStarts=0, integratedServerStops=1, errorEvents=0))


if __name__ == '__main__':
    unittest.main()

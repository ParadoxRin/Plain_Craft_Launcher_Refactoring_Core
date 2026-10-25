"""Read Minecraft XML and loader plain-text log events without counting stacktrace text as events."""
import re


def world_events(text):
    """Normalize message/severity from actual event headers, then count world lifecycle records."""
    events = []
    xml = re.compile(r'<log4j:Event\b[^>]*level="([^"\n]+)"[^>]*>\s*<log4j:Message><!\[CDATA\[(.*?)\]\]></log4j:Message>', re.S)
    for match in xml.finditer(text):
        events.append((match.group(1), match.group(2)))
    plain = re.compile(r'^\[\d{2}:\d{2}:\d{2}\] \[[^\n]*?/(INFO|WARN|ERROR|FATAL|DEBUG|TRACE)\](?: \[[^\n]*?\])?: (.*)$', re.M)
    for match in plain.finditer(text):
        events.append((match.group(1), match.group(2)))
    return {
        'integratedServerStarts': sum('Starting integrated minecraft server' in message for _, message in events),
        'integratedServerStops': sum(message.strip() == 'Stopping server' for _, message in events),
        'errorEvents': sum(level in ('ERROR', 'FATAL') for level, _ in events),
    }


def overworld_regions(inventory):
    """Accept the legacy and official 26.1+ Overworld layout; do not count entity/POI files."""
    prefixes = ('region/', 'dimensions/minecraft/overworld/region/')
    return [name for name in inventory if name.startswith(prefixes) and name.endswith('.mca')]

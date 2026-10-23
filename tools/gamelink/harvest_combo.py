"""Name more event/field ids by hashing word combinations (run after harvest_hashes.py).

Names are CamelCase compounds of a small vocabulary ("On" + "Player" + "Deactivate"), and
DJB2-xor (h = h*33 ^ c, seed 5381) extends one string at a time, so whole vocabularies
of two- and three-word candidates can be hashed in numpy and matched against the ids the
EBX dumps leave unnamed.

usage: python harvest_combo.py [--top 3000] [--triple-vocab 600]
       appends matches to event_hashes_extra.csv, each name prefixed "~"

These are GUESSES. A 32-bit hash matched by chance is expected about
candidates x unknown_ids / 2^32 times: ~70 for 9M pairs against 33k ids, but ~1,450 for
an open 187M-triple search - so triples are limited to an event-style first word and a
smaller vocabulary (~110 expected), and every combination result carries a "~" so the
feeds show it as a guess. (2026-10-08: the open triple search named 2,820, visibly
including nonsense like "AmountSetzspeederbike".)
"""
import argparse, os, re
from collections import Counter
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
UNKNOWN = os.path.join(HERE, 'unknown_ids.txt')
NAMES = os.path.join(HERE, 'known_names.txt')
OUT = os.path.join(HERE, 'event_hashes_extra.csv')
SPLIT = re.compile(r'[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|\d+')
EVENT_STARTS = ['On', 'Is', 'Set', 'Get', 'Enable', 'Disable', 'Start', 'Stop', 'Show', 'Hide', 'Reset',
                'Add', 'Remove', 'Activate', 'Deactivate', 'Begin', 'End', 'Open', 'Close', 'Trigger',
                'Play', 'Spawn', 'Kill', 'Can', 'Has', 'Use', 'Toggle', 'Clear', 'Update', 'Fire',
                'Request', 'Force', 'Apply', 'Send', 'Load', 'Unload', 'Enter', 'Exit', 'Player']


def extend(h, word):
    """Extend a vector of DJB2 states by the characters of word."""
    for c in word.encode('latin1', 'replace'):
        h = (h * np.uint32(33)) ^ np.uint32(c)
    return h


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--top', type=int, default=3000, help='vocabulary size for pairs')
    ap.add_argument('--triple-vocab', type=int, default=600, help='second/third-word size for triples')
    a = ap.parse_args()

    unknown = np.array(sorted(int(x, 16) for x in open(UNKNOWN).read().split()), dtype=np.uint32)
    names = [s for s in open(NAMES, encoding='utf-8').read().splitlines() if s]
    counts = Counter()
    for n in names:
        counts.update(SPLIT.findall(n))
    vocab = [w for w, _ in counts.most_common(a.top)]
    print(f'{len(unknown)} unknown ids, {len(names)} names, vocabulary {len(vocab)}')

    found = {}
    def collect(hashes, labels):
        hit = np.isin(hashes, unknown)
        for i in np.nonzero(hit)[0]:
            found.setdefault(int(hashes[i]), labels(i))

    seed = np.full(len(vocab), 5381, dtype=np.uint32)
    first = np.array([int(extend(np.array([5381], dtype=np.uint32), w)[0]) for w in vocab], dtype=np.uint32)
    with np.errstate(over='ignore'):
        # pairs: every vocab word followed by every vocab word
        for j, w2 in enumerate(vocab):
            h = extend(first.copy(), w2)
            collect(h, lambda i, w2=w2: vocab[i] + w2)
        print(f'pairs: {len(found)} named')
        # triples: an event-style first word, then two of the most common words
        k = min(a.triple_vocab, len(vocab))
        small = vocab[:k]
        prefix = np.array([int(extend(extend(np.array([5381], dtype=np.uint32), p), w)[0])
                           for p in EVENT_STARTS for w in small], dtype=np.uint32)
        for w3 in small:
            h = extend(prefix.copy(), w3)
            collect(h, lambda i, w3=w3: EVENT_STARTS[i // k] + small[i % k] + w3)
        print(f'triples: {len(found)} named (pairs included)')

    have = set()
    if os.path.exists(OUT):
        have = {int(r.split(',')[0], 16) for r in open(OUT, encoding='utf-8').read().splitlines()[1:] if r}
    added = 0
    with open(OUT, 'a', encoding='utf-8') as f:
        for h, s in sorted(found.items()):
            if h in have:
                continue
            f.write('0x%08X,%d,~%s\n' % (h, h - (1 << 32) if h >= 1 << 31 else h, s))
            added += 1
    print(f'appended {added} names to {OUT}')


if __name__ == '__main__':
    main()

#!/usr/bin/env python3
import json
from collections import defaultdict
from pathlib import Path

# Define the files to analyze
files = [
    "unity/Emberfall/Assets/CardData/M1.cards.json",
    "unity/Emberfall/Assets/CardData/M2.cards.json",
    "unity/Emberfall/Assets/CardData/M3.cards.json"
]

# Track data across all files
all_data = {}
all_card_ids = defaultdict(list)
required_fields = {'id', 'name', 'cost', 'class'}

for filepath in files:
    full_path = Path(filepath)

    # Read the JSON file
    with open(full_path, 'r', encoding='utf-8') as f:
        data = json.load(f)

    all_data[filepath] = data

# Now analyze
print("=" * 80)
print("CARD DATA INVENTORY ANALYSIS")
print("=" * 80)
print()

# Section 1: Total card entries per file
print("SECTION 1: Total Card Entries Per File")
print("-" * 80)
total_all = 0
totals_per_file = {}
for filepath in files:
    count = len(all_data[filepath])
    totals_per_file[filepath] = count
    total_all += count
    print(f"{filepath}: {count} cards")
print(f"\nCOMBINED TOTAL: {total_all} cards")
print()

# Section 2: Breakdown by class per file
print("SECTION 2: Breakdown by Class Per File")
print("-" * 80)
for filepath in files:
    class_breakdown = defaultdict(int)
    for card in all_data[filepath]:
        card_class = card.get('class', 'UNKNOWN')
        class_breakdown[card_class] += 1

    class_sum = sum(class_breakdown.values())
    print(f"\n{filepath}:")
    for card_class in sorted(class_breakdown.keys()):
        print(f"  {card_class}: {class_breakdown[card_class]}")
    print(f"  (Subtotal: {class_sum})")

print()

# Section 3: Breakdown by rarity per file
print("SECTION 3: Breakdown by Rarity Per File")
print("-" * 80)
for filepath in files:
    rarity_breakdown = defaultdict(int)
    for card in all_data[filepath]:
        rarity = card.get('rarity', 'UNKNOWN')
        rarity_breakdown[rarity] += 1

    rarity_sum = sum(rarity_breakdown.values())
    print(f"\n{filepath}:")
    for rarity in sorted(rarity_breakdown.keys()):
        print(f"  {rarity}: {rarity_breakdown[rarity]}")
    print(f"  (Subtotal: {rarity_sum})")

print()

# Section 4: Duplicate card IDs across all files
print("SECTION 4: Duplicate Card IDs Across All Files")
print("-" * 80)

# Collect all card IDs with their source files
for filepath in files:
    for card in all_data[filepath]:
        card_id = card.get('id', 'MISSING')
        all_card_ids[card_id].append(filepath)

# Find duplicates
duplicates = {card_id: files for card_id, files in all_card_ids.items() if len(files) > 1}

if duplicates:
    print("DUPLICATE CARD IDs FOUND:")
    for card_id in sorted(duplicates.keys()):
        sources = duplicates[card_id]
        print(f"  {card_id}: appears in {sources}")
else:
    print("No duplicate card IDs found across all three files.")

print()

# Section 5: Missing required fields
print("SECTION 5: Entries Missing Required Fields")
print("-" * 80)

missing_count = 0
for filepath in files:
    missing_in_file = []
    for idx, card in enumerate(all_data[filepath]):
        missing = required_fields - set(card.keys())
        if missing:
            card_id = card.get('id', f'[index {idx}]')
            missing_in_file.append({
                'card_id': card_id,
                'missing_fields': sorted(missing)
            })
            missing_count += 1

    if missing_in_file:
        print(f"\n{filepath}:")
        for entry in missing_in_file:
            print(f"  Card ID {entry['card_id']}: missing {entry['missing_fields']}")

if missing_count == 0:
    print("No entries missing required fields found in any file.")

print()
print("=" * 80)

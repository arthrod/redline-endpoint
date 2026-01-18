# Word "Unreadable Content" Diagnosis

This directory contains unpacked and formatted DOCX files for manual comparison and diagnosis.

## Files

- `generated.docx` - The redlined document produced by our API
- `word_fixed.docx` - The same document after Word "repaired" it
- `generated/` - Unpacked and XML-formatted contents of generated.docx
- `word_fixed/` - Unpacked and XML-formatted contents of word_fixed.docx

## Key Differences Found

### 1. XML Declaration (cosmetic)
- Generated: `<?xml version="1.0" encoding="utf-8"?>`
- Word-fixed: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>`

**Status**: Likely not the cause (encoding casing and standalone attribute are cosmetic)

### 2. Paragraph IDs (w14:paraId, w14:textId)
Word adds these attributes to every `<w:p>` element:
```xml
<!-- Generated -->
<w:p>

<!-- Word-fixed -->
<w:p w14:paraId="4B9BA395" w14:textId="77777777" w:rsidR="006E1EB0" w:rsidRDefault="00000000">
```

**Status**: Potential cause - Word may require these for tracked changes documents

### 3. Date Format in Tracked Changes
- Generated: `w:date="2026-01-16T13:38:14.1458250-05:00"` (ISO 8601 with timezone offset, high precision)
- Word-fixed: `w:date="2026-01-16T08:20:00Z"` (ISO 8601 UTC, second precision)

**Status**: Potential cause - date format or precision may be non-compliant

### 4. Attribute Ordering in w:del/w:ins
- Generated: `<w:del w:author="..." w:date="..." w:id="...">`
- Word-fixed: `<w:del w:id="..." w:author="..." w:date="...">`

**Status**: Unlikely cause (XML attribute order shouldn't matter)

### 5. Text Run Consolidation
Word merges adjacent text runs:
```xml
<!-- Generated: Two separate ins elements -->
<w:ins w:id="15">...<w:t>significados:</w:t>...</w:ins>
<w:ins w:id="16">...<w:t>blalblablalblalbalblablalbalbla</w:t>...</w:ins>

<!-- Word-fixed: Single merged element -->
<w:ins w:id="15">...<w:t>significados:blalblablalblalbalblablalbalbla</w:t>...</w:ins>
```

**Status**: Unlikely cause (Word normalizes this during save)

### 6. settings.xml Additions
Word adds document IDs:
```xml
<w14:docId w14:val="2FA9A355"/>
<w15:docId w15:val="{886FBE06-A171-B94D-BCD5-3E0EDE71F1BD}"/>
```

**Status**: Potential cause - Word may require these for proper document identification

## Diagnostic Experiments

To isolate the cause, try these experiments by modifying `generated/` files and repacking:

### Experiment 1: Add w14:paraId to all paragraphs
Add `w14:paraId` and `w14:textId` attributes to all `<w:p>` elements in document.xml

### Experiment 2: Normalize date format
Change dates from timezone offset format to UTC with second precision

### Experiment 3: Add document IDs
Add `<w14:docId>` and `<w15:docId>` to settings.xml

## Repacking Instructions

To repack a modified directory back into a DOCX:

```bash
cd diagnosis/generated
zip -r ../test_modified.docx . -x "*.DS_Store"
```

Then test `test_modified.docx` in Word to see if the warning persists.

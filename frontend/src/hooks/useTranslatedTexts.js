import { useMemo } from 'react'

function normalizeTexts(texts) {
  const unique = new Map()
  for (const item of texts || []) {
    const key = String(item?.key || '').trim()
    const value = String(item?.value || '').trim()
    if (key && value) unique.set(key, value)
  }
  return Object.fromEntries(unique)
}

// Content translation was removed: always return the original text.
export default function useTranslatedTexts(texts) {
  const signature = JSON.stringify(normalizeTexts(texts))
  const translations = useMemo(() => JSON.parse(signature), [signature])
  return { translations, translating: false, translationError: '' }
}

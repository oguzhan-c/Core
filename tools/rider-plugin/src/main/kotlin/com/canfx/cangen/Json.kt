package com.canfx.cangen

/**
 * can-gen'in --json çıktısını okumak için küçük bir JSON ayrıştırıcı (dış kütüphane kullanılmaz).
 * Sonuç: Map<String, Any?>, List<Any?>, String, Double, Boolean ya da null.
 */
internal object Json {
    fun parse(text: String): Any? = Parser(text).run {
        val value = readValue()
        skipWhitespace()
        if (index != text.length) error("JSON sonunda fazla karakter")
        value
    }

    private class Parser(val text: String) {
        var index = 0

        fun readValue(): Any? {
            skipWhitespace()
            if (index >= text.length) error("JSON beklenmedik şekilde bitti")
            return when (val c = text[index]) {
                '{' -> readObject()
                '[' -> readArray()
                '"' -> readString()
                't' -> literal("true", true)
                'f' -> literal("false", false)
                'n' -> literal("null", null)
                else -> if (c == '-' || c.isDigit()) readNumber() else error("Beklenmeyen karakter '$c' ($index)")
            }
        }

        private fun readObject(): Map<String, Any?> {
            val result = LinkedHashMap<String, Any?>()
            index++
            skipWhitespace()
            if (peek() == '}') { index++; return result }
            while (true) {
                skipWhitespace()
                val key = readString()
                skipWhitespace()
                expect(':')
                result[key] = readValue()
                skipWhitespace()
                when (text[index++]) {
                    ',' -> continue
                    '}' -> return result
                    else -> error("',' ya da '}' bekleniyordu (${index - 1})")
                }
            }
        }

        private fun readArray(): List<Any?> {
            val result = ArrayList<Any?>()
            index++
            skipWhitespace()
            if (peek() == ']') { index++; return result }
            while (true) {
                result.add(readValue())
                skipWhitespace()
                when (text[index++]) {
                    ',' -> continue
                    ']' -> return result
                    else -> error("',' ya da ']' bekleniyordu (${index - 1})")
                }
            }
        }

        private fun readString(): String {
            expect('"')
            val builder = StringBuilder()
            while (true) {
                val c = text[index++]
                when (c) {
                    '"' -> return builder.toString()
                    '\\' -> {
                        when (val e = text[index++]) {
                            '"', '\\', '/' -> builder.append(e)
                            'b' -> builder.append('\b')
                            'f' -> builder.append('\u000C')
                            'n' -> builder.append('\n')
                            'r' -> builder.append('\r')
                            't' -> builder.append('\t')
                            'u' -> {
                                builder.append(text.substring(index, index + 4).toInt(16).toChar())
                                index += 4
                            }
                            else -> error("Geçersiz kaçış karakteri '\\$e'")
                        }
                    }
                    else -> builder.append(c)
                }
            }
        }

        private fun readNumber(): Double {
            val start = index
            while (index < text.length && (text[index].isDigit() || text[index] in "+-.eE")) index++
            return text.substring(start, index).toDouble()
        }

        private fun literal(word: String, value: Any?): Any? {
            if (!text.startsWith(word, index)) error("'$word' bekleniyordu ($index)")
            index += word.length
            return value
        }

        private fun expect(c: Char) {
            if (peek() != c) error("'$c' bekleniyordu ($index)")
            index++
        }

        private fun peek(): Char? = text.getOrNull(index)

        fun skipWhitespace() {
            while (index < text.length && text[index].isWhitespace()) index++
        }
    }
}

@Suppress("UNCHECKED_CAST")
internal fun Any?.obj(): Map<String, Any?> = this as? Map<String, Any?> ?: emptyMap()

internal fun Any?.arr(): List<Any?> = this as? List<Any?> ?: emptyList()

internal fun Map<String, Any?>.str(key: String): String? = this[key] as? String

internal fun Map<String, Any?>.bool(key: String): Boolean = this[key] as? Boolean ?: false

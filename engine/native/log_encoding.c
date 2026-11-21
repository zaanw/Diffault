#include <stddef.h>
#include <stdint.h>

/* Return the encoding of a recognized byte-order mark. */
int errorhunt_log_encoding(const uint8_t *bytes, size_t length)
{
    if (length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
        return 1;
    if (length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
        return 2;
    if (length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
        return 3;
    return 0;
}

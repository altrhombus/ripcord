#include "rc_text.h"

#include <ctype.h>
#include <string.h>

int rc_text_equals_ci(const char *a, const char *b, size_t length)
{
    size_t i;
    for (i = 0; i < length; i++) {
        if (tolower((unsigned char)a[i]) != tolower((unsigned char)b[i]))
            return 0;
    }
    return 1;
}

void rc_text_trim(const char **start, const char **end)
{
    while (*start < *end && isspace((unsigned char)**start))
        (*start)++;
    while (*end > *start && isspace((unsigned char)*(*end - 1)))
        (*end)--;
}

void rc_text_copy_trimmed(const char *start, const char *end, char *dst, size_t dst_size)
{
    size_t length;

    rc_text_trim(&start, &end);
    length = (size_t)(end - start);
    if (length >= dst_size)
        length = dst_size - 1;
    memcpy(dst, start, length);
    dst[length] = '\0';
}

int rc_text_field_is(const char *start, const char *end, const char *name)
{
    size_t name_len = strlen(name);

    rc_text_trim(&start, &end);
    if ((size_t)(end - start) != name_len)
        return 0;
    return rc_text_equals_ci(start, name, name_len);
}

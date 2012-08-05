﻿using System.Collections.Generic;
using System.Security.Cryptography;

namespace System.Linq
{
    public static class EnumerableFx
    {
        public static IEnumerable<T> Append<T>(this IEnumerable<T> source, params T[] item)
        {
            return source.Concat(item);
        }

        public static IOrderedEnumerable<TSource> Shuffle<TSource>(this IEnumerable<TSource> source)
        {
            return Shuffle(source, new RNGCryptoServiceProvider());
        }

        public static IOrderedEnumerable<TSource> Shuffle<TSource>(this IEnumerable<TSource> source,
            RandomNumberGenerator rng)
        {
            if (source == null)
                throw new ArgumentNullException("source");

            var bytes = new byte[4];

            return source.OrderBy(delegate(TSource e)
            {
                rng.GetBytes(bytes);

                return BitConverter.ToInt32(bytes, 0);
            });
        }

        public static IEnumerable<T> WhereDo<T>(this IEnumerable<T> source, Func<T, bool> condition, Action<T> passed)
        {
            foreach (var v in source)
            {
                if (condition(v))
                    passed(v);
                yield return v;
            }
        }

        public static IEnumerable<T> Steal<T>(this IEnumerable<T> source, Func<T, bool> condition, Action<T> passed)
        {
            foreach (var v in source)
            {
                if (condition(v))
                    passed(v);
                else
                    yield return v;
            }
        }

        public static IEnumerable<T> Guard<T>(this IEnumerable<T> source)
        {
            return source ?? new T[0];
        }

        public static IEnumerable<T> Guard<T>(this IEnumerable<T> source, Func<bool> guard)
        {
            if (guard())
                return source;
            else
                return new T[0];
        }

        public static IEnumerable<T> Guard<T>(this IEnumerable<T> source, Func<IEnumerable<T>, bool> guard)
        {
            if (guard(source))
                return source;
            else
                return new T[0];
        }

        public static IEnumerable<TResult> Singlize<TSource, TIntermediate, TResult>(this IEnumerable<TSource> source,
            Func<IEnumerable<TSource>, TIntermediate> generator, Func<TSource, TIntermediate, TResult> mapper)
        {
            var cached = source.Share();
            var i = generator(cached);
            return cached.Select(s => mapper(s, i));
        }

        public static TResult Using<T, TResult>(this T disposable, Func<T, TResult> func) where T : IDisposable
        {
            using (disposable) return func(disposable);
        }

        public static string JoinString(this IEnumerable<string> source, string separator)
        {
            return String.Join(separator, source);
        }
    }
}

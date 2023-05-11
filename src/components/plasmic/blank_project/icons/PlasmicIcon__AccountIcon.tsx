// @ts-nocheck
/* eslint-disable */
/* tslint:disable */
/* prettier-ignore-start */
import React from "react";
import { classNames } from "@plasmicapp/react-web";

export type AccountIconIconProps = React.ComponentProps<"svg"> & {
  title?: string;
};

export function AccountIconIcon(props: AccountIconIconProps) {
  const { className, style, title, ...restProps } = props;
  return (
    <svg
      xmlns={"http://www.w3.org/2000/svg"}
      fill={"none"}
      viewBox={"0 0 28 29"}
      height={"1em"}
      width={"1em"}
      className={classNames("plasmic-default__svg", className)}
      style={style}
      {...restProps}
    >
      {title && <title>{title}</title>}

      <path
        d={
          "M13.99 14.802a6.3 6.3 0 100-12.6 6.3 6.3 0 000 12.6zM14 0a8.4 8.4 0 014.256 15.644c5.163 1.553 9.04 5.905 9.737 11.31.066.512-.316.979-.853 1.042-.538.063-1.027-.302-1.093-.814-.755-5.867-5.836-10.284-11.998-10.284-6.195 0-11.342 4.426-12.096 10.284-.066.512-.555.877-1.093.814-.537-.063-.919-.53-.853-1.043.694-5.382 4.594-9.727 9.764-11.296A8.4 8.4 0 0114 0z"
        }
        fill={"currentColor"}
      ></path>
    </svg>
  );
}

export default AccountIconIcon;
/* prettier-ignore-end */
